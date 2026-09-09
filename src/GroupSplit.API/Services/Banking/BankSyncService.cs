using System.Diagnostics.CodeAnalysis;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// How a run of <see cref="IBankSyncService.SyncAsync"/> ended.
/// </summary>
public enum SyncOutcome
{
    /// <summary>Every page applied; the cursor moved.</summary>
    Completed,

    /// <summary>Another run holds the connection. It will see everything this one would have.</summary>
    AlreadyRunning,

    /// <summary>No such connection, or one that is not <see cref="BankConnectionStatus.Active"/>.</summary>
    NotSyncable,

    /// <summary>The connector said the token no longer works; the connection is marked.</summary>
    LoginRequired,

    /// <summary>
    /// A transient failure part way. The rows already applied stay; the cursor did not
    /// move, so the next run walks the same pages over them.
    /// </summary>
    Interrupted
}

public interface IBankSyncService
{
    /// <summary>
    /// Pulls everything the provider has for one connection since its cursor into the
    /// staging rows. Idempotent: running it twice over the same pages changes nothing.
    /// </summary>
    Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct = default);
}

/// <summary>
/// The sync engine: provider-neutral, and the only code that ever holds an access token in
/// the clear.
/// </summary>
/// <remarks>
/// The rules it applies are the Phase 3 plan's, and are worth having in one place:
/// <list type="bullet">
/// <item>A row is recognised by the provider's id within its account, so a page replayed
/// after a crash finds its rows already here and overwrites them with themselves.</item>
/// <item>A posted row that names the pending row it settles takes that row's status --
/// and, when the status is <see cref="BankTransactionStatus.Filed"/>, the expense's link
/// -- and the pending row becomes <see cref="BankTransactionStatus.Superseded"/>. Both are
/// kept.</item>
/// <item>A removal deletes a row nobody acted on, stamps a filed one, and leaves a
/// superseded one to its replacement.</item>
/// <item>Modifying a row never touches the expense it became. Filing copies, then links.</item>
/// <item>Rows are saved per page; the cursor moves only once the provider has no more.</item>
/// </list>
/// </remarks>
public sealed class BankSyncService(
    AppDbContext dbContext,
    IServiceProvider services,
    IAccessTokenProtector protector,
    BankSyncLocks locks,
    IMerchantDirectory merchants,
    TimeProvider clock,
    ILogger<BankSyncService> logger) : IBankSyncService
{
    /// <summary>
    /// How many times a run starts over on the provider's say-so before giving up for
    /// today. A provider that keeps mutating under a run is not going to stop because we
    /// keep asking.
    /// </summary>
    private const int MaxRestarts = 3;

    public async Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct = default)
    {
        using var held = locks.TryHold(connectionId);

        if (held is null)
        {
            logger.LogDebug("Bank connection {ConnectionId} is already being synced.", connectionId);
            return SyncOutcome.AlreadyRunning;
        }

        var connection = await dbContext.Set<BankConnection>()
            .Include(candidate => candidate.Accounts)
            .FirstOrDefaultAsync(candidate => candidate.Id == connectionId, ct);

        if (connection is null)
        {
            logger.LogWarning("Bank connection {ConnectionId} was queued for sync but does not exist.", connectionId);
            return SyncOutcome.NotSyncable;
        }

        if (connection.Status != BankConnectionStatus.Active)
        {
            logger.LogInformation("Bank connection {ConnectionId} is {Status}; not syncing.", connectionId, connection.Status);
            return SyncOutcome.NotSyncable;
        }

        if (services.GetKeyedService<IBankConnector>(connection.Provider) is not { } connector)
        {
            // A provider this deployment does not speak: one switched off, one removed, or
            // the demo data the seeder writes. Not an error to throw on -- the nightly sweep
            // walks every connection, and one it cannot sync must not stop the ones it can.
            logger.LogInformation(
                "Bank connection {ConnectionId} is with {Provider}, which nothing here speaks; not syncing.",
                connectionId, connection.Provider);

            return SyncOutcome.NotSyncable;
        }

        // What this run is about. Nothing stops somebody re-linking the same bank while it is
        // in flight, and a re-link moves the connection onto a new item: new id, new token,
        // no cursor. Every write this run makes afterwards would be about an item that is no
        // longer the connection's -- a cursor from a retired item, or a LoginRequired status
        // earned by a token that has just been handed back -- and either one leaves a freshly
        // linked connection unable to sync at all. So each write checks first.
        var item = connection.ProviderItemId;

        string accessToken;

        try
        {
            accessToken = protector.Unprotect(connection.AccessTokenCiphertext);
        }
        catch (AccessTokenUnreadableException e)
        {
            // The key changed, or the row did. Nothing here can recover the token -- that is
            // what encrypting it means -- so the connection is marked and the person is
            // asked to link the bank again, which mints a new one. The alternative is a
            // sweep that throws on this connection every day forever.
            logger.LogError(e, "Bank connection {ConnectionId}: the stored access token cannot be read.", connectionId);

            await MarkAsync(connection, item, BankConnectionStatus.LoginRequired, ct);

            return SyncOutcome.LoginRequired;
        }

        var accounts = connection.Accounts.ToDictionary(account => account.ProviderAccountId);

        // Only after a re-link, and only until this run finishes. See BankConnection.AccountsRekeyed.
        var rekeying = connection.AccountsRekeyed
            ? await Rekeying.LoadAsync(dbContext, accounts, logger, ct)
            : null;

        var startCursor = connection.Cursor;
        var cursor = startCursor;
        var restarts = 0;

        while (true)
        {
            SyncPage page;

            try
            {
                page = await connector.SyncAsync(accessToken, cursor, ct);
            }
            catch (BankSyncException e) when (e.Kind == BankSyncFailure.LoginRequired)
            {
                logger.LogInformation(e, "Bank connection {ConnectionId} needs the person to sign in again.", connectionId);

                await MarkAsync(connection, item, BankConnectionStatus.LoginRequired, ct);

                return SyncOutcome.LoginRequired;
            }
            catch (BankSyncException e) when (e.Kind == BankSyncFailure.RestartFromCursor && restarts < MaxRestarts)
            {
                restarts++;
                logger.LogInformation(e, "Bank connection {ConnectionId}: restarting the page run from its starting cursor ({Attempt}/{Max}).",
                    connectionId, restarts, MaxRestarts);
                cursor = startCursor;
                continue;
            }
            catch (BankSyncException e)
            {
                logger.LogWarning(e, "Bank connection {ConnectionId}: sync interrupted; the cursor stays where the run began.", connectionId);
                return SyncOutcome.Interrupted;
            }

            await ApplyAsync(connection, accounts, rekeying, page, ct);

            cursor = page.NextCursor;

            if (!page.HasMore)
                break;
        }

        if (await SupersededAsync(connection, item, ct))
        {
            // Re-linked underneath this run. The cursor belongs to the item that has just
            // been retired, and writing it would leave the new item resuming from somewhere
            // it has never been -- which the provider refuses, for ever, on every sync from
            // then on. The rows this run imported are kept: they are this account's spending
            // whichever item reported them.
            return SyncOutcome.Interrupted;
        }

        connection.Cursor = cursor;
        connection.LastSyncedAt = clock.GetUtcNow();

        // The catch-up run is over, so the licence to match on appearance ends with it. Only
        // on a completed run: one that was interrupted has not seen the whole history yet,
        // and the rows it did not reach still have to be recognised next time.
        if (rekeying is not null)
        {
            connection.AccountsRekeyed = false;
            rekeying.Report(connection.Id);
        }

        await dbContext.SaveChangesAsync(ct);

        return SyncOutcome.Completed;
    }

    /// <summary>
    /// Whether the connection has moved onto a different provider item since this run read
    /// it, which is what a re-link underneath a sync looks like from in here.
    /// </summary>
    /// <remarks>
    /// Re-read from the database, because a re-link commits through its own scope and this
    /// run's copy would otherwise never hear about it. Both callers reload before they write
    /// anything to the connection, so nothing pending is thrown away by doing so.
    /// <para>
    /// A row that has gone entirely -- unlinked while this ran -- counts as superseded too.
    /// The reload detaches it rather than failing, and writing to a detached entity is a
    /// silent no-op that would have this run report success for work nothing kept.
    /// </para>
    /// </remarks>
    private async Task<bool> SupersededAsync(BankConnection connection, string item, CancellationToken ct)
    {
        var entry = dbContext.Entry(connection);

        await entry.ReloadAsync(ct);

        if (entry.State is EntityState.Detached)
        {
            logger.LogInformation(
                "Bank connection {ConnectionId} was removed while it was syncing, so what this run had to say "
                + "about it is being dropped.", connection.Id);

            return true;
        }

        if (connection.ProviderItemId == item)
            return false;

        logger.LogInformation(
            "Bank connection {ConnectionId} moved from item {Item} to {Current} while it was syncing, so what "
            + "this run had to say about the old one is being dropped.",
            connection.Id, item, connection.ProviderItemId);

        return true;
    }

    /// <summary>Writes a status, unless the connection has been re-linked underneath.</summary>
    private async Task MarkAsync(
        BankConnection connection, string item, BankConnectionStatus status, CancellationToken ct)
    {
        if (await SupersededAsync(connection, item, ct))
            return;

        connection.Status = status;
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        Rekeying? rekeying, SyncPage page, CancellationToken ct)
    {
        var index = await PageIndex.LoadAsync(dbContext, accounts, page, ct);

        // A row this page already names by id is spoken for, whichever order the page lists
        // things in. That is what stops a resumed run double-claiming: it meets the rows its
        // first attempt re-keyed, finds them by their new ids, and would otherwise still
        // have them queued as unclaimed history -- free to be handed to some other
        // transaction with the same date, amount and description, which loses that
        // transaction and re-points this one.
        //
        // Per page, and so is the protection: a row whose new id turns up only in a later
        // page is claimable while the earlier ones run. That is the design's own accepted
        // cost -- an exact match on account, date, amount and description between two
        // different transactions -- rather than something this closes.
        rekeying?.Exclude(index.Rows);

        var now = clock.GetUtcNow();

        foreach (var imported in page.Added)
            await AddAsync(connection, accounts, index, rekeying, imported, now, ct);

        foreach (var imported in page.Modified)
            await ModifyAsync(connection, accounts, index, rekeying, imported, now, ct);

        foreach (var removed in page.Removed)
            Remove(connection, accounts, index, removed, now);

        await dbContext.SaveChangesAsync(ct);
    }

    private async Task AddAsync(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        PageIndex index, Rekeying? rekeying, ImportedTransaction imported, DateTimeOffset now, CancellationToken ct)
    {
        if (!accounts.TryGetValue(imported.ProviderAccountId, out var account))
        {
            SkipUnknownAccount(connection, imported.ProviderAccountId);
            return;
        }

        // Already here: a replayed page. What arrived is the truth about the row; what was
        // done with it stays done.
        if (index.Find(account.Id, imported.ProviderTransactionId) is { } existing)
        {
            await OverwriteAsync(existing, imported, ct);
            return;
        }

        BankTransaction row;

        // Not here under that id, which after a re-link does not mean not here. A row this
        // connection already holds is claimed and re-keyed rather than inserted a second
        // time, so whatever was done with it -- filed into a group, ignored -- stays done.
        if (rekeying?.Claim(account.Id, imported) is { } carried)
        {
            // Out under the id it had before it is put back under the new one. The old entry
            // is real: a resumed run loads rows by the ids the page names, which after a
            // first attempt includes ids this run assigned.
            index.Forget(carried);

            carried.ProviderTransactionId = imported.ProviderTransactionId;

            row = carried;
        }
        else
        {
            // The required members plus what identifies the row. Everything else the
            // provider said is written by Overwrite on the next line, for both branches.
            row = new BankTransaction
            {
                Account = account,
                LinkedAccountId = account.Id,
                ProviderTransactionId = imported.ProviderTransactionId,
                Date = imported.Date,
                Amount = imported.Amount,
                Description = Clip(imported.Description, 256),
                RawJson = imported.RawJson,
                ImportedAt = now
            };

            dbContext.Add(row);
        }

        await OverwriteAsync(row, imported, ct);
        index.Add(row);

        // Shared by both, and it was not: a re-keyed row used to return before this, so a
        // posted transaction that both claimed a carried row and named the pending row it
        // settles established no chain -- and that pending row was then inserted as a fresh
        // one, showing in the inbox as new money for spending already filed.
        if (imported.ReplacesProviderTransactionId is { } pendingId
            && index.Find(account.Id, pendingId) is { Status: not BankTransactionStatus.Superseded } pending
            && pending != row)
        {
            await SupersedeAsync(pending, row, ct);
        }
    }

    private async Task ModifyAsync(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        PageIndex index, Rekeying? rekeying, ImportedTransaction imported, DateTimeOffset now, CancellationToken ct)
    {
        if (!accounts.TryGetValue(imported.ProviderAccountId, out var account))
        {
            SkipUnknownAccount(connection, imported.ProviderAccountId);
            return;
        }

        var row = index.Find(account.Id, imported.ProviderTransactionId);

        // A modification to a row never seen is a row: the add and the change arrived in
        // one page, or the add was lost. Either way what is wanted is the row as it is now.
        if (row is null)
        {
            await AddAsync(connection, accounts, index, rekeying, imported, now, ct);
            return;
        }

        await OverwriteAsync(row, imported, ct);
    }

    private void Remove(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        PageIndex index, RemovedTransaction removed, DateTimeOffset now)
    {
        if (!accounts.TryGetValue(removed.ProviderAccountId, out var account))
        {
            SkipUnknownAccount(connection, removed.ProviderAccountId);
            return;
        }

        var row = index.Find(account.Id, removed.ProviderTransactionId);

        if (row is null)
        {
            logger.LogDebug("Bank connection {ConnectionId}: removal of a row never imported ({ProviderTransactionId}).",
                connection.Id, removed.ProviderTransactionId);
            return;
        }

        switch (row.Status)
        {
            case BankTransactionStatus.New:
            case BankTransactionStatus.Ignored:
                // Nobody acted on it, so nothing points at it and nothing misses it.
                dbContext.Remove(row);
                index.Forget(row);
                break;

            case BankTransactionStatus.Filed:
                // The expense it became is somebody's history. Keep the row, say what
                // happened to it, and let the inbox tell them.
                row.RemovedAt ??= now;
                break;

            case BankTransactionStatus.Superseded:
                // The provider removes the pending row when its posted row arrives; the
                // posted row holds the story now.
                break;
        }
    }

    /// <summary>
    /// The posted row takes over from the pending one: its status, and when that status is
    /// filed, the expense's link. The pending row stays, marked, so the chain reads.
    /// </summary>
    /// <remarks>
    /// The status the chain ends on is the stronger of the two, never simply the pending
    /// one's. Copying it across was safe only while the posted row was always brand new --
    /// New is the floor, so the copy could only ever raise it. A posted row can now be one
    /// this connection already held, claimed by a re-key, and such a row carries a real
    /// decision: pending New over a claimed Filed row puts an expense's own row back in the
    /// inbox as unfiled, and pending Ignored over one does the same by way of the Ignored
    /// tab's Restore. Either way somebody can file a second expense for money already
    /// recorded, and the duplicate check cannot warn about it -- it compares against
    /// expenses that came from no bank row, and this one came from this one.
    /// <para>
    /// Written as a precedence rather than as an inequality on purpose. Three statuses meet
    /// here and the interesting pairs are the ones nobody thinks of; a rule that says which
    /// of any two wins is checkable by reading it, where "not New" was checkable only by
    /// walking the cases, and missed one.
    /// </para>
    /// </remarks>
    private async Task SupersedeAsync(BankTransaction pending, BankTransaction posted, CancellationToken ct)
    {
        posted.Replaces = pending;
        posted.ReplacesId = pending.Id;

        // Read before the status moves, because afterwards the two are indistinguishable.
        var postedCarriesItsOwn = posted.Status is BankTransactionStatus.Filed;

        posted.Status = Stronger(pending.Status, posted.Status);

        if (pending.Status is not BankTransactionStatus.Filed)
        {
            // Nothing to move: only a filed row has an expense pointing at it.
        }
        else if (postedCarriesItsOwn)
        {
            // Both filed, which means two expenses and one link to hold them. The
            // relationship is one to one, so moving the second onto the posted row is a
            // constraint violation rather than a merge. The chain still reads; the expenses
            // stay where they are, and this says so.
            logger.LogWarning(
                "Rows {PendingId} and {PostedId} are both filed and the second supersedes the first, so the "
                + "first expense keeps pointing at the row it was filed from.", pending.Id, posted.Id);
        }
        else
        {
            var filed = await dbContext.Set<Transaction>()
                .Where(transaction => transaction.BankTransactionId == pending.Id)
                .ToListAsync(ct);

            foreach (var transaction in filed)
            {
                transaction.BankTransaction = posted;
                transaction.BankTransactionId = posted.Id;
            }
        }

        pending.Status = BankTransactionStatus.Superseded;
    }

    /// <summary>
    /// Which of two statuses a supersede chain should end on: the one carrying the most
    /// decision. Filed beats Ignored beats New, and Superseded loses to everything -- it is
    /// a statement about a row that has been taken over, not about what anybody decided.
    /// </summary>
    private static BankTransactionStatus Stronger(BankTransactionStatus first, BankTransactionStatus second) =>
        Decision(first) >= Decision(second) ? first : second;

    private static int Decision(BankTransactionStatus status) => status switch
    {
        BankTransactionStatus.Filed => 3,
        BankTransactionStatus.Ignored => 2,
        BankTransactionStatus.New => 1,
        BankTransactionStatus.Superseded => 0,
        _ => 0
    };

    /// <summary>
    /// What the provider says about the row, onto the row. Never the status, never
    /// <c>ImportedAt</c>: those are ours.
    /// </summary>
    /// <remarks>
    /// The merchant is the one thing here that is not a column copy: the name comes off the
    /// row and the row comes away pointing at a shared <see cref="Merchant"/>, so the logo
    /// is stored once for the place rather than once per payment. A row the provider stops
    /// naming a merchant on goes back to pointing at nothing.
    /// </remarks>
    private async Task OverwriteAsync(BankTransaction row, ImportedTransaction imported, CancellationToken ct)
    {
        row.Date = imported.Date;
        row.Amount = imported.Amount;
        row.Currency = imported.Currency;
        row.Description = Clip(imported.Description, 256);
        row.MerchantName = Clip(imported.MerchantName, 128);
        row.ProviderCategory = Clip(imported.ProviderCategory, 64);
        row.ProviderCategoryDetailed = Clip(imported.ProviderCategoryDetailed, 96);
        row.AuthorizedDate = imported.AuthorizedDate;
        row.PaymentChannel = Clip(imported.PaymentChannel, 32);
        row.City = Clip(imported.City, 64);
        row.CategoryIconUrl = Clip(imported.CategoryIconUrl, 512);
        row.Pending = imported.Pending;
        row.RawJson = imported.RawJson;

        var merchant = await merchants.ResolveAsync(imported.MerchantName, imported.LogoUrl, ct);

        row.Merchant = merchant;
        row.MerchantId = merchant?.Id;
    }

    private void SkipUnknownAccount(BankConnection connection, string providerAccountId) =>
        logger.LogWarning(
            "Bank connection {ConnectionId}: a row on an account this connection does not know ({ProviderAccountId}); skipped. Picking up a new account is a re-link, not a sync.",
            connection.Id, providerAccountId);

    /// <summary>
    /// The columns have lengths and the provider's strings do not. The full text is in the
    /// raw payload for anyone who needs it.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? Clip(string? value, int maxLength) =>
        value is { Length: var length } && length > maxLength ? value[..maxLength] : value;

    /// <summary>
    /// The rows a connection already held when it was moved onto a new provider item, so a
    /// page run that meets them again under new ids can recognise them.
    /// </summary>
    /// <remarks>
    /// Matched on account, date, amount and description together, and never on any subset.
    /// A looser key would be more forgiving of a provider that words a description slightly
    /// differently across items, and the trade runs the wrong way: a miss costs one
    /// duplicate row in somebody's inbox, which is what happens today anyway, while a false
    /// match re-keys a stored row onto a transaction that is not it -- and the real one is
    /// then never stored at all. A row is claimed at most once for the same reason.
    /// <para>
    /// Loaded once for the whole run rather than queried per row: it is the connection's own
    /// history, the run is re-reading all of it regardless, and matching has to hold across
    /// pages.
    /// </para>
    /// </remarks>
    private sealed class Rekeying
    {
        private readonly Dictionary<(Guid AccountId, DateOnly Date, decimal Amount, string Description),
            List<BankTransaction>> _carried = new();

        /// <summary>
        /// Rows that are no longer available to be claimed: either claimed already, or named
        /// by id in a page and therefore not history looking for an owner.
        /// </summary>
        private readonly HashSet<Guid> _spokenFor = [];

        private int _claimed;

        private ILogger _logger = null!;

        public static async Task<Rekeying> LoadAsync(AppDbContext dbContext,
            Dictionary<string, LinkedAccount> accounts, ILogger logger, CancellationToken ct)
        {
            var accountIds = accounts.Values.Select(account => account.Id).ToList();

            var rows = await dbContext.Set<BankTransaction>()
                .Where(row => accountIds.Contains(row.LinkedAccountId)
                              // A pending row the posted one already took over from shares
                              // its date, amount and description exactly -- so with both in
                              // here the replay lands on whichever came first, and half the
                              // time that is the superseded one: invisible in the inbox, and
                              // the real row left holding an id the provider has forgotten.
                              // Same for a row the provider has already withdrawn.
                              && row.Status != BankTransactionStatus.Superseded
                              && row.RemovedAt == null)
                // Ordered, so which of two identical-looking rows is claimed first is a
                // decision rather than whatever the database felt like returning.
                .OrderBy(row => row.Date)
                .ThenBy(row => row.Id)
                .ToListAsync(ct);

            var rekeying = new Rekeying { _logger = logger };

            foreach (var row in rows)
            {
                if (!rekeying._carried.TryGetValue(Key(row), out var carried))
                    rekeying._carried[Key(row)] = carried = [];

                carried.Add(row);
            }

            logger.LogInformation(
                "Re-keying {Count} stored rows onto a newly linked item; they will be recognised by what "
                + "they look like rather than by an id the provider has changed.", rows.Count);

            return rekeying;
        }

        /// <summary>
        /// Takes rows out of the running because a page already names them by id.
        /// </summary>
        public void Exclude(IEnumerable<BankTransaction> known)
        {
            foreach (var row in known)
                _spokenFor.Add(row.Id);
        }

        /// <summary>The stored row this reported one is, if it is one of them. Once each.</summary>
        public BankTransaction? Claim(Guid accountId, ImportedTransaction imported)
        {
            var key = (accountId, imported.Date, imported.Amount, Clip(imported.Description, 256));

            if (!_carried.TryGetValue(key, out var carried))
                return null;

            if (carried.FirstOrDefault(row => !_spokenFor.Contains(row.Id)) is not { } claimed)
                return null;

            _spokenFor.Add(claimed.Id);
            _claimed++;

            return claimed;
        }

        public void Report(Guid connectionId)
        {
            var left = _carried.Values.Sum(
                carried => carried.Count(row => !_spokenFor.Contains(row.Id)));

            _logger.LogInformation(
                "Bank connection {ConnectionId}: re-keyed {Claimed} stored rows onto the new item; {Left} were "
                + "not reported again and keep the ids they had.", connectionId, _claimed, left);
        }

        private static (Guid, DateOnly, decimal, string) Key(BankTransaction row) =>
            (row.LinkedAccountId, row.Date, row.Amount, row.Description);
    }

    /// <summary>
    /// Every row a page could refer to, read once per page instead of once per row, plus
    /// the rows the page itself adds.
    /// </summary>
    private sealed class PageIndex
    {
        private readonly Dictionary<(Guid AccountId, string ProviderId), BankTransaction> _rows = new();

        public static async Task<PageIndex> LoadAsync(AppDbContext dbContext,
            Dictionary<string, LinkedAccount> accounts, SyncPage page, CancellationToken ct)
        {
            var index = new PageIndex();

            var providerIds = page.Added.Select(row => row.ProviderTransactionId)
                .Concat(page.Added.Select(row => row.ReplacesProviderTransactionId).OfType<string>())
                .Concat(page.Modified.Select(row => row.ProviderTransactionId))
                .Concat(page.Modified.Select(row => row.ReplacesProviderTransactionId).OfType<string>())
                .Concat(page.Removed.Select(row => row.ProviderTransactionId))
                .Distinct()
                .ToList();

            if (providerIds.Count == 0)
                return index;

            var accountIds = accounts.Values.Select(account => account.Id).ToList();

            var rows = await dbContext.Set<BankTransaction>()
                .Where(row => accountIds.Contains(row.LinkedAccountId) && providerIds.Contains(row.ProviderTransactionId))
                .ToListAsync(ct);

            foreach (var row in rows)
                index.Add(row);

            return index;
        }

        /// <summary>Every row this page already knows about, however it found them.</summary>
        public IEnumerable<BankTransaction> Rows => _rows.Values;

        public BankTransaction? Find(Guid accountId, string providerId) =>
            _rows.GetValueOrDefault((accountId, providerId));

        public void Add(BankTransaction row) => _rows[(row.LinkedAccountId, row.ProviderTransactionId)] = row;

        public void Forget(BankTransaction row) => _rows.Remove((row.LinkedAccountId, row.ProviderTransactionId));
    }
}

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

        var connector = services.GetKeyedService<IBankConnector>(connection.Provider)
                        ?? throw new InvalidOperationException(
                            $"No bank connector is registered for provider \"{connection.Provider}\".");

        var accessToken = protector.Unprotect(connection.AccessTokenCiphertext);
        var accounts = connection.Accounts.ToDictionary(account => account.ProviderAccountId);

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
                connection.Status = BankConnectionStatus.LoginRequired;
                await dbContext.SaveChangesAsync(ct);
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

            await ApplyAsync(connection, accounts, page, ct);

            cursor = page.NextCursor;

            if (!page.HasMore)
                break;
        }

        connection.Cursor = cursor;
        connection.LastSyncedAt = clock.GetUtcNow();
        await dbContext.SaveChangesAsync(ct);

        return SyncOutcome.Completed;
    }

    private async Task ApplyAsync(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        SyncPage page, CancellationToken ct)
    {
        var index = await PageIndex.LoadAsync(dbContext, accounts, page, ct);
        var now = clock.GetUtcNow();

        foreach (var imported in page.Added)
            await AddAsync(connection, accounts, index, imported, now, ct);

        foreach (var imported in page.Modified)
            await ModifyAsync(connection, accounts, index, imported, now, ct);

        foreach (var removed in page.Removed)
            Remove(connection, accounts, index, removed, now);

        await dbContext.SaveChangesAsync(ct);
    }

    private async Task AddAsync(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        PageIndex index, ImportedTransaction imported, DateTimeOffset now, CancellationToken ct)
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
            Overwrite(existing, imported);
            return;
        }

        var row = new BankTransaction
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

        Overwrite(row, imported);

        dbContext.Add(row);
        index.Add(row);

        if (imported.ReplacesProviderTransactionId is { } pendingId
            && index.Find(account.Id, pendingId) is { Status: not BankTransactionStatus.Superseded } pending)
        {
            await SupersedeAsync(pending, row, ct);
        }
    }

    private async Task ModifyAsync(BankConnection connection, Dictionary<string, LinkedAccount> accounts,
        PageIndex index, ImportedTransaction imported, DateTimeOffset now, CancellationToken ct)
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
            await AddAsync(connection, accounts, index, imported, now, ct);
            return;
        }

        Overwrite(row, imported);
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
    private async Task SupersedeAsync(BankTransaction pending, BankTransaction posted, CancellationToken ct)
    {
        posted.Replaces = pending;
        posted.ReplacesId = pending.Id;
        posted.Status = pending.Status;

        if (pending.Status == BankTransactionStatus.Filed)
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
    /// What the provider says about the row, onto the row. Never the status, never
    /// <c>ImportedAt</c>: those are ours.
    /// </summary>
    private static void Overwrite(BankTransaction row, ImportedTransaction imported)
    {
        row.Date = imported.Date;
        row.Amount = imported.Amount;
        row.Currency = imported.Currency;
        row.Description = Clip(imported.Description, 256);
        row.MerchantName = Clip(imported.MerchantName, 128);
        row.ProviderCategory = Clip(imported.ProviderCategory, 64);
        row.Pending = imported.Pending;
        row.RawJson = imported.RawJson;
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

        public BankTransaction? Find(Guid accountId, string providerId) =>
            _rows.GetValueOrDefault((accountId, providerId));

        public void Add(BankTransaction row) => _rows[(row.LinkedAccountId, row.ProviderTransactionId)] = row;

        public void Forget(BankTransaction row) => _rows.Remove((row.LinkedAccountId, row.ProviderTransactionId));
    }
}

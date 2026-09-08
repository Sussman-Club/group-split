using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Banking;

public interface IBankConnectionService
{
    /// <summary>
    /// The caller's linked banks, and whether linking one is possible at all in this
    /// deployment.
    /// </summary>
    Task<BankConnectionsResponse> Mine(CancellationToken ct = default);

    /// <summary>
    /// A token for the provider's linking UI. Naming a connection opens it in update mode,
    /// which is how a bank asking for a fresh sign-in gets repaired.
    /// </summary>
    Task<LinkTokenResponse> CreateLinkToken(LinkTokenRequest request, string? webhookUrl, string? redirectUri,
        CancellationToken ct = default);

    /// <summary>
    /// Turns what the linking UI handed back into a stored connection and asks for its
    /// first sync. Linking a bank that is already linked returns the existing connection
    /// rather than making a second copy of it.
    /// </summary>
    Task<BankConnection> Link(CreateBankConnectionRequest request, CancellationToken ct = default);

    /// <summary>Queues a sync. Returns once it is queued; nothing waits on the sync itself.</summary>
    Task Sync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Tells the provider the connection is over and deletes it with its accounts and
    /// imported rows. Expenses filed from those rows stay, and lose only the link back.
    /// </summary>
    Task Unlink(Guid id, CancellationToken ct = default);

    /// <summary>The connection a provider's webhook named, or null if this is not ours.</summary>
    Task<BankConnection?> ForProviderItem(string provider, string providerItemId, CancellationToken ct = default);
}

/// <summary>
/// Linking, unlinking and asking for syncs. Everything a person does to a bank connection
/// rather than to the rows it brings in.
/// </summary>
/// <remarks>
/// A connection belongs to a person, so every read starts from the caller and another
/// person's connection is a 404 rather than a 403: whether it exists is not theirs to
/// learn. The access token is protected on the way in and is never read here.
/// </remarks>
public sealed class BankConnectionService(
    ICurrentUser userContext,
    AppDbContext dbContext,
    IServiceProvider services,
    IAccessTokenProtector protector,
    IJobDispatcher jobs,
    TimeProvider clock,
    IOptions<BankingOptions> options,
    ILogger<BankConnectionService> logger) : IBankConnectionService
{
    /// <summary>How long a best-effort retirement gets before it is given up on.</summary>
    private static readonly TimeSpan RetireTimeout = TimeSpan.FromSeconds(10);

    private string DefaultProvider => options.Value.Provider;

    public async Task<BankConnectionsResponse> Mine(CancellationToken ct = default)
    {
        var connections = await Owned()
            .Include(connection => connection.Accounts)
            .OrderBy(connection => connection.InstitutionName)
            .ToListAsync(ct);

        return new BankConnectionsResponse(
            Connector(DefaultProvider) is not null,
            connections.Select(Describe).ToList());
    }

    public async Task<LinkTokenResponse> CreateLinkToken(LinkTokenRequest request, string? webhookUrl,
        string? redirectUri, CancellationToken ct = default)
    {
        // Update mode is asked for by naming a connection, and it is that connection's
        // provider that has to open it -- not this deployment's current default.
        var connection = request.ConnectionId is { } id ? await Existing(id, ct) : null;
        var provider = connection?.Provider ?? DefaultProvider;
        var connector = Required(provider);

        var session = await Call(provider, "create a link token", () => connector.CreateLinkSessionAsync(
            new LinkSessionRequest(
                userContext.User.Id.ToString(),
                webhookUrl,
                redirectUri,
                connection is null ? null : protector.Unprotect(connection.AccessTokenCiphertext)),
            ct));

        return new LinkTokenResponse(session.Token, session.ExpiresAt);
    }

    public async Task<BankConnection> Link(CreateBankConnectionRequest request, CancellationToken ct = default)
    {
        var provider = DefaultProvider;
        var connector = Required(provider);

        var item = await Call(provider, "exchange the public token", () => connector.ExchangeAsync(request.PublicToken, ct));

        var existing = await dbContext.Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .FirstOrDefaultAsync(
                connection => connection.Provider == provider && connection.ProviderItemId == item.ProviderItemId, ct);

        // Linking the same bank twice -- ordinary, when somebody repairs a connection by
        // starting again rather than through update mode -- is the same connection with a
        // fresh token, not a second one whose rows would duplicate the first's.
        if (existing is not null)
        {
            if (existing.UserId != userContext.User.Id)
            {
                // The provider handed us an item that belongs to somebody else's account
                // here. Nothing good comes of joining those two together -- and nothing
                // retires it either, unlike the paths below: it is the item their
                // connection is using, and removing it would break theirs, not ours.
                logger.LogWarning("Bank item {ProviderItemId} is already linked to another account.", item.ProviderItemId);
                throw new NotFoundException(ErrorCodes.BankConnectionNotFound, "That bank connection is not available.");
            }

            existing.AccessTokenCiphertext = protector.Protect(item.AccessToken);
            existing.InstitutionName = item.InstitutionName;
            existing.Status = BankConnectionStatus.Active;

            MergeAccounts(existing, item.Accounts);

            // Also not retired on failure: this is the item the connection already names,
            // so removing it would take a working connection with it rather than clean up
            // after a failed one.
            await dbContext.SaveChangesAsync(ct);
            await jobs.DispatchAsync(new SyncBankConnection(existing.Id), ct);

            return existing;
        }

        // Not an item of ours, which is not the same as not a bank of theirs. A provider
        // mints a fresh item every time somebody links, so two links to one bank agree on
        // no id at all and the match above cannot see them -- which is exactly the case its
        // own comment describes, somebody repairing by starting again. Left there, the
        // person gets a second copy of a bank they already had, the rows arrive twice, and
        // an item is spent at the provider for nothing.
        if (await SameBankAlreadyLinked(provider, item, ct) is { } duplicate)
        {
            logger.LogInformation(
                "Linking {InstitutionName} again as item {ProviderItemId}; adopting it onto connection {ConnectionId}.",
                item.InstitutionName, item.ProviderItemId, duplicate.Id);

            // Read before Adopt overwrites it, so the log below names the item that was
            // actually retired rather than the one that replaced it.
            var supersededItemId = duplicate.ProviderItemId;
            var superseded = Adopt(duplicate, item);

            await StoringOrRetiring(provider, item, () => dbContext.SaveChangesAsync(ct));

            // Only now. As of that save the connection names the new item, so the one it
            // replaced is finally nobody's. The other order -- which this had -- retires the
            // old item first, and a save that then fails leaves the connection pointing at
            // something the provider has already removed: unsyncable, unrepairable, and with
            // no token left anywhere that could remove it either.
            if (superseded is not null)
            {
                await TryRetire(provider, superseded, supersededItemId);
            }

            await jobs.DispatchAsync(new SyncBankConnection(duplicate.Id), ct);

            return duplicate;
        }

        var created = new BankConnection
        {
            User = userContext.User,
            Provider = provider,
            ProviderItemId = item.ProviderItemId,
            InstitutionName = item.InstitutionName,
            AccessTokenCiphertext = protector.Protect(item.AccessToken),
            LinkedAt = clock.GetUtcNow()
        };

        MergeAccounts(created, item.Accounts);

        dbContext.Add(created);

        await StoringOrRetiring(provider, item, () => dbContext.SaveChangesAsync(ct));

        // After the save, so a dispatch that fails leaves a stored connection the nightly
        // sweep will pick up rather than an item retired out from under one.
        await jobs.DispatchAsync(new SyncBankConnection(created.Id), ct);

        return created;
    }

    /// <summary>
    /// Runs the write that stores a freshly linked item, and retires the item at the
    /// provider if that write does not happen.
    /// </summary>
    /// <remarks>
    /// The item exists at the provider from the moment somebody finished linking, which is
    /// before this application hears anything at all -- and its token is only in hand for
    /// the length of this request. So a failure between the exchange and the save is the
    /// expensive one: the item is live, counted, and about to become unnameable, because the
    /// one token that could ever have retired it is the one being dropped. Retiring it here
    /// is the last moment that is possible.
    /// <para>
    /// Only for an item that is new to us. An item some connection already names is not
    /// ours to remove on the way out of a failure -- see the two paths above that say so.
    /// </para>
    /// </remarks>
    private async Task StoringOrRetiring(string provider, LinkedItem item, Func<Task<int>> write)
    {
        try
        {
            await write();
        }
        catch
        {
            await TryRetire(provider, item.AccessToken, item.ProviderItemId);

            throw;
        }
    }

    /// <summary>
    /// Tells the provider an item is over, and carries on if it will not listen.
    /// </summary>
    /// <remarks>
    /// Best effort everywhere it is called from: every caller has already decided that what
    /// it was doing stands whether or not this works. A provider that refuses leaves an item
    /// running there, which is worth a line in the log and is not worth undoing a link over.
    /// </remarks>
    private async Task TryRetire(string provider, string accessToken, string itemId)
    {
        // Deliberately not the request's cancellation token. This runs *because* something
        // went wrong, and a caller who disconnected mid-link is one of the likelier reasons
        // -- handing it the token that was just cancelled would cancel the cleanup too, at
        // exactly the moment the cleanup is the only thing that can still name the item. A
        // short budget of its own instead, because nothing is waiting on the answer.
        using var budget = new CancellationTokenSource(RetireTimeout);

        try
        {
            if (Connector(provider) is { } connector)
            {
                await connector.RemoveAsync(accessToken, budget.Token);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not retire item {ProviderItemId} at {Provider}. It may still be counted there.",
                itemId, provider);
        }
    }

    public async Task Sync(Guid id, CancellationToken ct = default)
    {
        var connection = await Existing(id, ct);

        if (connection.Status != BankConnectionStatus.Active)
        {
            throw new ConflictException(ErrorCodes.BankConnectionNeedsAttention,
                "This bank needs you to sign in again before it can be synced.");
        }

        await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);
    }

    public async Task Unlink(Guid id, CancellationToken ct = default)
    {
        var connection = await Existing(id, ct);

        // The whole graph, so EF deletes it rather than leaving it to the database's own
        // cascade. One person's one bank is a bounded number of rows, and the alternative
        // is a delete that behaves differently depending on the provider underneath -- which
        // is exactly the kind of difference that passes every test and surprises somebody
        // in production.
        await dbContext.Entry(connection).Collection(candidate => candidate.Accounts).LoadAsync(ct);

        foreach (var account in connection.Accounts)
            await dbContext.Entry(account).Collection(candidate => candidate.Transactions).LoadAsync(ct);

        // Expenses filed from those rows keep everything except the link back. Said here
        // rather than left to the foreign key's own set-null, for the same reason: this is
        // a promise the app makes to somebody about their spending history, and it should
        // not depend on which database is underneath.
        var importedIds = connection.Accounts
            .SelectMany(account => account.Transactions)
            .Select(row => row.Id)
            .ToList();

        var filed = await dbContext.Set<Transaction>()
            .Where(transaction => transaction.BankTransactionId != null
                                  && importedIds.Contains(transaction.BankTransactionId.Value))
            .ToListAsync(ct);

        foreach (var transaction in filed)
        {
            transaction.BankTransaction = null;
            transaction.BankTransactionId = null;
        }

        // Told first, deleted second. A provider that refuses leaves the connection here,
        // which the person can try again; deleting first and failing to tell them would
        // leave an item nobody owns still being billed for and still sending webhooks.
        if (Connector(connection.Provider) is { } connector)
        {
            await Call(connection.Provider, "remove the item", async () =>
            {
                await connector.RemoveAsync(protector.Unprotect(connection.AccessTokenCiphertext), ct);
                return true;
            });
        }
        else
        {
            logger.LogWarning(
                "Unlinking connection {ConnectionId} without telling {Provider}: no connector is registered for it.",
                connection.Id, connection.Provider);
        }

        // The expenses filed from this connection's rows keep everything but the link: the
        // relationship is SetNull, so they are spending history rather than an import.
        dbContext.Remove(connection);
        await dbContext.SaveChangesAsync(ct);
    }

    public Task<BankConnection?> ForProviderItem(string provider, string providerItemId, CancellationToken ct = default) =>
        dbContext.Set<BankConnection>()
            .FirstOrDefaultAsync(
                connection => connection.Provider == provider && connection.ProviderItemId == providerItemId, ct);

    public static BankConnectionResponse Describe(BankConnection connection) =>
        new(connection.Id,
            connection.Provider,
            connection.InstitutionName,
            (BankConnectionState)connection.Status,
            connection.LinkedAt,
            connection.LastSyncedAt,
            connection.Accounts
                .OrderBy(account => account.Name)
                .Select(account => new LinkedAccountResponse(
                    account.Id, account.Name, account.Mask, account.Type, account.Subtype, account.Currency))
                .ToList());

    private IQueryable<BankConnection> Owned() =>
        dbContext.Set<BankConnection>().Where(connection => connection.UserId == userContext.User.Id);

    private async Task<BankConnection> Existing(Guid id, CancellationToken ct) =>
        await Owned().Include(connection => connection.Accounts).FirstOrDefaultAsync(connection => connection.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.BankConnectionNotFound, "Bank connection not found.");

    private IBankConnector? Connector(string provider) => services.GetKeyedService<IBankConnector>(provider);

    private IBankConnector Required(string provider) =>
        Connector(provider)
        ?? throw new ConflictException(ErrorCodes.BankSyncUnavailable,
            "Bank sync is not configured for this deployment.");

    /// <summary>
    /// Everything that leaves this process for a provider goes through here, so a provider
    /// having a bad minute reaches the caller as one code rather than as a 500.
    /// </summary>
    private async Task<T> Call<T>(string provider, string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (BankSyncException e)
        {
            logger.LogWarning(e, "{Provider} could not {What}.", provider, what);
            throw new BadGatewayException(ErrorCodes.BankProviderUnavailable,
                "The bank service could not be reached. Please try again shortly.", e);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning(e, "{Provider} could not {What}.", provider, what);
            throw new BadGatewayException(ErrorCodes.BankProviderUnavailable,
                "The bank service could not be reached. Please try again shortly.", e);
        }
    }

    /// <summary>
    /// Keeps the accounts the provider reports, adding new ones and refreshing the names of
    /// the ones already here. Nothing is removed: rows point at accounts, and an account
    /// the provider stopped listing still explains where last month's coffee came from.
    /// </summary>
    /// <summary>
    /// The connection this freshly linked item duplicates, if it duplicates one.
    /// </summary>
    /// <remarks>
    /// Matched on the bank and the accounts behind it rather than on any id, because there
    /// is no id the two share: a provider mints account ids per item exactly as it mints the
    /// item id. What does survive a re-link is what the bank shows the person -- the mask,
    /// and failing that the account's name -- so that is what is compared.
    /// <para>
    /// One account in common is enough. Two items over one login report the same accounts,
    /// while somebody who genuinely holds two separate logins at one bank has none in
    /// common, and gets the second connection they asked for.
    /// </para>
    /// </remarks>
    private async Task<BankConnection?> SameBankAlreadyLinked(
        string provider, LinkedItem item, CancellationToken ct)
    {
        var candidates = await dbContext.Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .Where(connection => connection.Provider == provider
                                 && connection.UserId == userContext.User.Id
                                 && connection.InstitutionName == item.InstitutionName)
            .ToListAsync(ct);

        return candidates.FirstOrDefault(candidate => candidate.Accounts
            .Any(stored => item.Accounts.Any(incoming => SameAccount(stored, incoming))));
    }

    private static bool SameAccount(LinkedAccount stored, ImportedAccount incoming)
    {
        if (stored.Type != incoming.Type)
            return false;

        // The bank's own last few digits, and the same whichever item asks for them.
        if (!string.IsNullOrWhiteSpace(stored.Mask) && !string.IsNullOrWhiteSpace(incoming.Mask))
            return stored.Mask == incoming.Mask;

        // Not every institution gives a mask, and then the name is all there is.
        return string.Equals(stored.Name, incoming.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves a connection onto a newly linked item. Answers the token of the item it
    /// replaced, for the caller to retire once the move has actually been stored, or null
    /// when that token cannot be read and so nothing can retire it.
    /// </summary>
    /// <remarks>
    /// Deliberately does not retire anything itself. It is called before the save, and the
    /// item it replaces must outlive the save -- otherwise a save that fails leaves the
    /// connection naming an item that has already been removed.
    /// </remarks>
    private string? Adopt(BankConnection connection, LinkedItem item)
    {
        string? superseded;

        try
        {
            superseded = protector.Unprotect(connection.AccessTokenCiphertext);
        }
        catch (AccessTokenUnreadableException e)
        {
            // Already unnameable, which is the state this whole path exists to stop new
            // items falling into. Nothing to do but say so and take the new item on.
            logger.LogWarning(e,
                "The token for superseded item {ProviderItemId} cannot be read, so it cannot be retired at {Provider}.",
                connection.ProviderItemId, connection.Provider);

            superseded = null;
        }

        connection.ProviderItemId = item.ProviderItemId;
        connection.AccessTokenCiphertext = protector.Protect(item.AccessToken);
        connection.InstitutionName = item.InstitutionName;
        connection.Status = BankConnectionStatus.Active;

        // The cursor is the old item's place in its own stream and means nothing to the new
        // one. Cleared, so the next sync starts from the beginning rather than resuming at
        // a point the provider has never heard of.
        connection.Cursor = null;

        AdoptAccounts(connection, item.Accounts);

        return superseded;
    }

    /// <summary>
    /// Points the accounts already stored at the new item's ids, so that everything filed
    /// against them keeps the account it was filed against.
    /// </summary>
    /// <remarks>
    /// <see cref="MergeAccounts"/> matches on the provider's account id, which is the right
    /// key when the item has not changed and the wrong one here: a new item renames every
    /// account, so matching on it would add a second row for each and orphan the history on
    /// the first.
    /// </remarks>
    private static void AdoptAccounts(BankConnection connection, IReadOnlyList<ImportedAccount> accounts)
    {
        foreach (var account in accounts)
        {
            var stored = connection.Accounts.FirstOrDefault(candidate => SameAccount(candidate, account));

            if (stored is null)
            {
                connection.Accounts.Add(new LinkedAccount
                {
                    ProviderAccountId = account.ProviderAccountId,
                    Name = account.Name,
                    Mask = account.Mask,
                    Type = account.Type,
                    Subtype = account.Subtype,
                    Currency = account.Currency
                });

                continue;
            }

            stored.ProviderAccountId = account.ProviderAccountId;
            stored.Name = account.Name;
            stored.Mask = account.Mask;
            stored.Subtype = account.Subtype;
            stored.Currency = account.Currency;
        }
    }

    private static void MergeAccounts(BankConnection connection, IReadOnlyList<ImportedAccount> accounts)
    {
        foreach (var account in accounts)
        {
            var existing = connection.Accounts
                .FirstOrDefault(candidate => candidate.ProviderAccountId == account.ProviderAccountId);

            if (existing is null)
            {
                connection.Accounts.Add(new LinkedAccount
                {
                    ProviderAccountId = account.ProviderAccountId,
                    Name = account.Name,
                    Mask = account.Mask,
                    Type = account.Type,
                    Subtype = account.Subtype,
                    Currency = account.Currency
                });

                continue;
            }

            existing.Name = account.Name;
            existing.Mask = account.Mask;
            existing.Type = account.Type;
            existing.Subtype = account.Subtype;
            existing.Currency = account.Currency;
        }
    }
}

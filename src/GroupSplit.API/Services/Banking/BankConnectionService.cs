using System.Text.Json;
using GroupSplit.API.Endpoints;
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
    Task<LinkTokenResponse> CreateLinkToken(LinkTokenRequest request, string? redirectUri,
        CancellationToken ct = default);

    /// <summary>
    /// Turns what the linking UI handed back into a stored connection and asks for its
    /// first sync. Linking a bank that is already linked returns the existing connection
    /// rather than making a second copy of it.
    /// </summary>
    Task<BankConnection> Link(CreateBankConnectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Finishes a link that was interrupted between the provider handing over its item and
    /// this application storing it. Does nothing if there is nothing left to finish.
    /// </summary>
    /// <remarks>
    /// Finishing costs no item at the provider where linking again costs one, which is the
    /// whole reason the interrupted link was written down rather than abandoned.
    /// </remarks>
    Task CompletePending(Guid pendingLinkId, CancellationToken ct = default);

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
    /// <summary>
    /// How a held item is written down and read back. The same options both ways, and only
    /// ever by this deployment: nothing outside reads this column.
    /// </summary>
    private static readonly JsonSerializerOptions ItemJson = new(JsonSerializerDefaults.Web);

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

    public async Task<LinkTokenResponse> CreateLinkToken(LinkTokenRequest request, string? redirectUri,
        CancellationToken ct = default)
    {
        // Update mode is asked for by naming a connection, and it is that connection's
        // provider that has to open it -- not this deployment's current default.
        var connection = request.ConnectionId is { } id ? await Existing(id, ct) : null;
        var provider = connection?.Provider ?? DefaultProvider;
        var connector = Required(provider);

        var session = await Call(provider, "create a link token", () => connector.CreateLinkSessionAsync(
            new LinkSessionRequest(
                userContext.User.Id.ToString(),
                WebhookUrlFor(provider),
                redirectUri,
                connection is null ? null : protector.Unprotect(connection.AccessTokenCiphertext)),
            ct));

        return new LinkTokenResponse(session.Token, session.ExpiresAt);
    }

    /// <summary>
    /// Where <paramref name="provider"/> should deliver this session's webhooks, or null when
    /// this deployment has no address to give -- which is the ordinary case locally, where
    /// nothing outside could reach it anyway.
    /// </summary>
    /// <remarks>
    /// Built here, rather than handed in by the route, because both halves are known here and
    /// only here. The origin is configuration, which this service already reads; the path
    /// names the provider, and the provider is the one resolved above -- in update mode the
    /// connection's, not this deployment's default. An address naming the wrong provider is a
    /// webhook the wrong connector's verifier would be handed, which refuses it, so that
    /// connection would simply never hear from its bank again.
    /// </remarks>
    private string? WebhookUrlFor(string provider) =>
        options.Value.PublicOrigin is { Length: > 0 } origin
            ? origin.TrimEnd('/') + WebhooksApi.PathFor(provider)
            : null;

    public async Task<BankConnection> Link(CreateBankConnectionRequest request, CancellationToken ct = default)
    {
        var provider = DefaultProvider;
        var connector = Required(provider);
        var user = userContext.User;

        // Written down before it can be lost. The provider's item already exists by the
        // time this request arrives, and the token that names it is handed over once, so
        // everything from here to a stored connection is a window in which losing it means
        // losing the item -- live at the provider, counted, and nameable by nothing.
        var pending = new PendingBankLink
        {
            User = user,
            Provider = provider,
            PublicTokenCiphertext = protector.Protect(request.PublicToken),
            StartedAt = clock.GetUtcNow()
        };

        dbContext.Add(pending);
        await dbContext.SaveChangesAsync(ct);

        var item = await Call(provider, "exchange the public token",
            () => connector.ExchangeAsync(request.PublicToken, ct));

        // The public token is spent now, and the item is what finishing this needs instead.
        // Retired if it cannot be written down, because at that point nothing else in the
        // world holds a token for it.
        pending.PublicTokenCiphertext = null;
        pending.ItemCiphertext = protector.Protect(JsonSerializer.Serialize(item, ItemJson));

        await RetiringIfNotStored(provider, item, () => dbContext.SaveChangesAsync(ct));

        BankConnection connection;

        try
        {
            connection = await Store(user, provider, item, ct);
        }
        catch (Exception e)
        {
            // Not retired, unlike every other failure after an exchange: the item is safely
            // written down, so this link can be finished later rather than started again --
            // and starting again would spend another of the provider's items where
            // finishing spends none. The sweep picks it up.
            logger.LogWarning(e,
                "Bank link {PendingLinkId} could not be stored yet. The item is held and will be finished.",
                pending.Id);

            throw new LeftBehindException(
                ErrorCodes.BankLinkWillBeFinished,
                "The bank connection could not be finished just now. The access it granted is held safely and "
                + "will be applied shortly; nothing needs doing.", e);
        }

        // Idempotent if this does not happen: a row whose connection already exists is
        // found again by the same matching below, which answers with that connection.
        dbContext.Remove(pending);
        await dbContext.SaveChangesAsync(ct);

        await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);

        return connection;
    }

    public async Task CompletePending(Guid pendingLinkId, CancellationToken ct = default)
    {
        var pending = await dbContext.Set<PendingBankLink>()
            .Include(link => link.User)
            .FirstOrDefaultAsync(link => link.Id == pendingLinkId, ct);

        if (pending is null)
        {
            // Finished by the request that started it, or by an earlier attempt.
            return;
        }

        pending.Attempts++;
        pending.LastAttemptAt = clock.GetUtcNow();
        await dbContext.SaveChangesAsync(ct);

        var item = await ItemFor(pending, ct);

        if (item is null)
        {
            return;
        }

        var connection = await Store(pending.User, pending.Provider, item, ct);

        dbContext.Remove(pending);
        await dbContext.SaveChangesAsync(ct);

        await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);

        logger.LogInformation(
            "Finished interrupted bank link {PendingLinkId} as connection {ConnectionId}.",
            pending.Id, connection.Id);
    }

    /// <summary>
    /// The exchanged item a pending link carries, exchanging its public token first if that
    /// has not happened yet. Null when neither is usable, which is the end of the road for
    /// that row.
    /// </summary>
    private async Task<LinkedItem?> ItemFor(PendingBankLink pending, CancellationToken ct)
    {
        if (pending.ItemCiphertext is { } stored)
        {
            try
            {
                return JsonSerializer.Deserialize<LinkedItem>(protector.Unprotect(stored), ItemJson);
            }
            catch (Exception e) when (e is AccessTokenUnreadableException or JsonException)
            {
                logger.LogError(e,
                    "The item held for bank link {PendingLinkId} cannot be read, so the link cannot be finished.",
                    pending.Id);

                return null;
            }
        }

        if (pending.PublicTokenCiphertext is not { } publicToken || Connector(pending.Provider) is not { } connector)
        {
            return null;
        }

        // Still worth trying: a public token outlives the request that fetched it by a few
        // minutes, and inside that window an interrupted link finishes without anybody
        // being asked to link their bank a second time.
        try
        {
            var item = await connector.ExchangeAsync(protector.Unprotect(publicToken), ct);

            pending.PublicTokenCiphertext = null;
            pending.ItemCiphertext = protector.Protect(JsonSerializer.Serialize(item, ItemJson));
            await dbContext.SaveChangesAsync(ct);

            return item;
        }
        catch (Exception e)
        {
            // Expired, most likely, which is ordinary and not worth an error: the person
            // links again, and that is the one case where an item really is spent.
            logger.LogWarning(e,
                "The public token held for bank link {PendingLinkId} could not be exchanged.", pending.Id);

            return null;
        }
    }

    /// <summary>
    /// Turns an exchanged item into a stored connection: the same work whether a request is
    /// waiting on it or the sweep is finishing what one left behind.
    /// </summary>
    /// <remarks>
    /// Takes the person rather than reading the current one, because the second caller has
    /// no request and therefore no current one.
    /// </remarks>
    private async Task<BankConnection> Store(
        User user, string provider, LinkedItem item, CancellationToken ct)
    {
        var existing = await dbContext.Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .FirstOrDefaultAsync(
                connection => connection.Provider == provider && connection.ProviderItemId == item.ProviderItemId, ct);

        // Linking the same bank twice -- ordinary, when somebody repairs a connection by
        // starting again rather than through update mode -- is the same connection with a
        // fresh token, not a second one whose rows would duplicate the first's.
        if (existing is not null)
        {
            if (existing.UserId != user.Id)
            {
                // The provider handed us an item that belongs to somebody else's account
                // here. Nothing good comes of joining those two together -- and nothing
                // retires it either, unlike the paths below: it is the item their
                // connection is using, and removing it would break theirs, not ours.
                logger.LogWarning("Bank item {ProviderItemId} is already linked to another account.",
                    item.ProviderItemId);
                throw new NotFoundException(ErrorCodes.BankConnectionNotFound,
                    "That bank connection is not available.");
            }

            existing.AccessTokenCiphertext = protector.Protect(item.AccessToken);
            existing.InstitutionName = item.InstitutionName;
            existing.Status = BankConnectionStatus.Active;

            MergeAccounts(existing, item.Accounts);

            await dbContext.SaveChangesAsync(ct);

            return existing;
        }

        // Not an item of ours, which is not the same as not a bank of theirs. A provider
        // mints a fresh item every time somebody links, so two links to one bank agree on
        // no id at all and the match above cannot see them -- which is exactly the case its
        // own comment describes, somebody repairing by starting again. Left there, the
        // person gets a second copy of a bank they already had, the rows arrive twice, and
        // an item is spent at the provider for nothing.
        if (await SameBankAlreadyLinked(user.Id, provider, item, ct) is { } duplicate)
        {
            logger.LogInformation(
                "Linking {InstitutionName} again as item {ProviderItemId}; adopting it onto connection {ConnectionId}.",
                item.InstitutionName, item.ProviderItemId, duplicate.Id);

            // Read before Adopt overwrites it, so the log below names the item that was
            // actually retired rather than the one that replaced it.
            var supersededItemId = duplicate.ProviderItemId;
            var superseded = Adopt(duplicate, item);

            await dbContext.SaveChangesAsync(ct);

            // Only now. As of that save the connection names the new item, so the one it
            // replaced is finally nobody's. The other order -- which this had -- retires the
            // old item first, and a save that then fails leaves the connection pointing at
            // something the provider has already removed: unsyncable, unrepairable, and with
            // no token left anywhere that could remove it either.
            if (superseded is not null)
            {
                await TryRetire(provider, superseded, supersededItemId);
            }

            return duplicate;
        }

        var created = new BankConnection
        {
            User = user,
            Provider = provider,
            ProviderItemId = item.ProviderItemId,
            InstitutionName = item.InstitutionName,
            AccessTokenCiphertext = protector.Protect(item.AccessToken),
            LinkedAt = clock.GetUtcNow()
        };

        MergeAccounts(created, item.Accounts);

        dbContext.Add(created);

        await dbContext.SaveChangesAsync(ct);

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
    private async Task RetiringIfNotStored(string provider, LinkedItem item, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception e)
        {
            var retired = await TryRetire(provider, item.AccessToken, item.ProviderItemId);

            // Said out loud rather than left as a bare 500. The bank granted access a
            // moment ago and this request then failed, so "something went wrong" would
            // leave somebody unable to tell whether their bank is now connected to an
            // application that cannot use it. The two codes differ only in whether the
            // access was handed back, which is the part they cannot find out anywhere else.
            throw new LeftBehindException(
                retired ? ErrorCodes.BankLinkNotSaved : ErrorCodes.BankLinkNotSavedAccessRemains,
                retired
                    ? "The bank connection could not be stored, so the access it granted was handed back. "
                      + "Nothing is linked."
                    : "The bank connection could not be stored, and the access it granted could not be "
                      + "handed back. Nothing is linked here.",
                e);
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
    /// <returns>Whether the provider was actually told.</returns>
    private async Task<bool> TryRetire(string provider, string accessToken, string itemId)
    {
        // Deliberately not the request's cancellation token. This runs *because* something
        // went wrong, and a caller who disconnected mid-link is one of the likelier reasons
        // -- handing it the token that was just cancelled would cancel the cleanup too, at
        // exactly the moment the cleanup is the only thing that can still name the item. A
        // short budget of its own instead, because nothing is waiting on the answer.
        using var budget = new CancellationTokenSource(RetireTimeout);

        try
        {
            if (Connector(provider) is not { } connector)
            {
                return false;
            }

            await connector.RemoveAsync(accessToken, budget.Token);

            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not retire item {ProviderItemId} at {Provider}. It may still be counted there.",
                itemId, provider);

            return false;
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
        await dbContext.Entry(connection)
            .Collection(candidate => candidate.Accounts)
            .Query()
            .Include(account => account.Transactions)
            .LoadAsync(ct);

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
            await Call(connection.Provider, "remove the item",
                () => connector.RemoveAsync(protector.Unprotect(connection.AccessTokenCiphertext), ct));
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

    public Task<BankConnection?> ForProviderItem(
        string provider, string providerItemId, CancellationToken ct = default) =>
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
            [
                .. connection.Accounts
                    .OrderBy(account => account.Name)
                    .Select(account => new LinkedAccountResponse(
                        account.Id, account.Name, account.Mask, account.Type, account.Subtype, account.Currency))
            ]);

    private IQueryable<BankConnection> Owned() =>
        dbContext.Set<BankConnection>().Where(connection => connection.UserId == userContext.User.Id);

    private async Task<BankConnection> Existing(Guid id, CancellationToken ct) =>
        await Owned().Include(connection => connection.Accounts)
            .FirstOrDefaultAsync(connection => connection.Id == id, ct)
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
        catch (Exception e) when (e is BankSyncException or HttpRequestException)
        {
            logger.LogWarning(e, "{Provider} could not {What}.", provider, what);

            throw new BadGatewayException(ErrorCodes.BankProviderUnavailable,
                "The bank service could not be reached. Please try again shortly.", e);
        }
    }

    /// <summary>
    /// The same translation for a call that answers nothing, so a caller does not have to
    /// invent a return value to get it.
    /// </summary>
    private Task Call(string provider, string what, Func<Task> call) =>
        Call<object?>(provider, what, async () =>
        {
            await call();

            return null;
        });

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
        Guid userId, string provider, LinkedItem item, CancellationToken ct)
    {
        var candidates = await dbContext.Set<BankConnection>()
            .Include(connection => connection.Accounts)
            .Where(connection => connection.Provider == provider
                                 && connection.UserId == userId
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
    /// the first. What survives a re-link is what the bank shows the person, so that is what
    /// is matched on instead.
    /// </remarks>
    private static void AdoptAccounts(BankConnection connection, IReadOnlyList<ImportedAccount> accounts) =>
        Reconcile(connection, accounts, SameAccount);

    /// <summary>
    /// Keeps the accounts the provider reports, adding new ones and refreshing the details of
    /// the ones already here. Nothing is removed: rows point at accounts, and an account the
    /// provider stopped listing still explains where last month's coffee came from.
    /// </summary>
    private static void MergeAccounts(BankConnection connection, IReadOnlyList<ImportedAccount> accounts) =>
        Reconcile(connection, accounts,
            (stored, incoming) => stored.ProviderAccountId == incoming.ProviderAccountId);

    /// <summary>
    /// The body both of those share: every reported account is either recognised by
    /// <paramref name="recognises"/> and brought up to date, or added.
    /// </summary>
    /// <remarks>
    /// A recognised account has every field written, the two keys included. That is what each
    /// caller already did: whichever key it matched on it left alone, and writing a value a
    /// match has just proved equal changes nothing -- so one body is the same work as two,
    /// without the second copy to keep in step.
    /// </remarks>
    private static void Reconcile(
        BankConnection connection,
        IReadOnlyList<ImportedAccount> accounts,
        Func<LinkedAccount, ImportedAccount, bool> recognises)
    {
        foreach (var account in accounts)
        {
            var stored = connection.Accounts.FirstOrDefault(candidate => recognises(candidate, account));

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
            stored.Type = account.Type;
            stored.Subtype = account.Subtype;
            stored.Currency = account.Currency;
        }
    }
}
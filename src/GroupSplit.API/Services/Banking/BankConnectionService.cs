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

    /// <summary>
    /// Gives up on a link that has been tried enough times, handing back the access it is
    /// holding rather than leaving a live token in a row nothing will ever read again.
    /// </summary>
    Task AbandonPending(Guid pendingLinkId, CancellationToken ct = default);

    /// <summary>Queues a sync. Returns once it is queued; nothing waits on the sync itself.</summary>
    Task Sync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Tells the provider the connection is over and deletes it with its accounts and
    /// imported rows. Expenses filed from those rows stay, and lose only the link back.
    /// </summary>
    Task Unlink(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Reads the provider's account list again for a connection that already exists, and
    /// asks for a sync.
    /// </summary>
    /// <remarks>
    /// What update mode ends in. Repairing or extending a connection through the linking
    /// UI mints no new item and hands back no usable public token, so nothing on the
    /// linking path runs -- and an account shared there would otherwise never be heard of.
    /// </remarks>
    Task<BankConnection> Refresh(Guid id, CancellationToken ct = default);

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
/// learn.
/// <para>
/// The access token is protected on the way in, and read back only where a provider has to
/// be handed it: opening update mode, retiring a superseded item, unlinking. Every one of
/// those reads goes through something that copes with a token the key ring can no longer
/// open, because that is a state a deployment can really be in and none of them may answer
/// it with an unhandled exception.
/// </para>
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

    /// <summary>
    /// How long un-counting an interrupted attempt gets. Shorter than a retirement, because
    /// this runs during shutdown and what it protects is worth one attempt of five.
    /// </summary>
    private static readonly TimeSpan HandBackTimeout = TimeSpan.FromSeconds(5);

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

        string? existingToken = null;

        if (connection is not null)
        {
            try
            {
                existingToken = protector.Unprotect(connection.AccessTokenCiphertext);
            }
            catch (AccessTokenUnreadableException e)
            {
                // Update mode needs the token to name the item it is repairing, so there is
                // nothing to repair with. Said rather than thrown as a bare 500: every other
                // reader of a stored token handles this deliberately, and the person clicking
                // "Sign in again" would otherwise get an opaque failure every time, on a
                // connection nothing had marked. Marked here, so the answer stops being a
                // surprise and the only way out -- linking the bank again -- is the one the
                // card offers.
                logger.LogError(e,
                    "Bank connection {ConnectionId}: the stored access token cannot be read, so it cannot be "
                    + "repaired in update mode.", connection.Id);

                connection.Status = BankConnectionStatus.LoginRequired;
                await dbContext.SaveChangesAsync(ct);

                throw new UnprocessableException(ErrorCodes.BankConnectionUnrecoverable,
                    "This connection cannot be repaired: the access your bank granted can no longer be read "
                    + "here. Remove it and link the bank again.");
            }
        }

        var session = await Call(provider, "create a link token", () => connector.CreateLinkSessionAsync(
            new LinkSessionRequest(
                userContext.User.Id.ToString(),
                WebhookUrlFor(provider),
                redirectUri,
                existingToken),
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
        catch (DomainException)
        {
            // A refusal rather than a mishap. Store has looked at this item and said no,
            // and it will say the same to every attempt the sweep makes -- so holding the
            // row would keep bank access alive in a table nothing surfaces, for a link that
            // is never going to be finished. Nothing is retired: the only thing Store
            // refuses an item for is already belonging to somebody else's connection, which
            // makes it theirs and in use.
            await Discard(pending, "the item is not this account's to store", ct);

            throw;
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

        // Counted before the work, not after, so that an attempt which dies halfway still
        // counts and a row that kills whatever picks it up cannot be retried for ever.
        pending.Attempts++;
        pending.LastAttemptAt = clock.GetUtcNow();
        await dbContext.SaveChangesAsync(ct);

        LinkedItem? item;
        BankConnection connection;

        try
        {
            item = await ItemFor(pending, ct);

            if (item is null)
            {
                return;
            }

            connection = await Store(pending.User, pending.Provider, item, ct);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. That says nothing about this link, so the attempt is
            // handed back: without this a restart loop spends five attempts on a perfectly
            // good item over five sweeps, and the fifth hands it to AbandonPending, which
            // retires at the provider an item somebody really did grant. That does not come
            // back.
            await HandBackAttempt(pendingLinkId);

            throw;
        }
        catch (DomainException e)
        {
            // The same refusal the request that started this would have been given, and it
            // will not read differently on the next attempt. Dropped rather than retried to
            // exhaustion, and for the same reason as in Link nothing is retired.
            logger.LogWarning(e,
                "Held bank link {PendingLinkId} was refused and cannot be finished.", pending.Id);

            await Discard(pending, "the item is not this account's to store", ct);

            return;
        }

        dbContext.Remove(pending);
        await dbContext.SaveChangesAsync(ct);

        await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);

        logger.LogInformation(
            "Finished interrupted bank link {PendingLinkId} as connection {ConnectionId}.",
            pending.Id, connection.Id);
    }

    /// <summary>
    /// Un-counts the attempt a cancelled run had already claimed, and nothing else.
    /// </summary>
    /// <remarks>
    /// The change tracker is emptied first, and that is the whole point of the method rather
    /// than an incidental tidy-up. A save is context-wide, so decrementing through the
    /// context the cancelled work was using would commit everything that work had begun --
    /// and on the adopt path what it had begun is a connection already pointing at the new
    /// item, with the old item's token overwritten and the retirement that was meant to
    /// follow the save never reached. That token existed only on that stack. Committing half
    /// of it strands the replaced item at the provider for good, where a rollback would have
    /// left the sweep a clean retry.
    /// <para>
    /// Its own token, because the one that was passed in is the one that was just cancelled,
    /// and its own try, because a failure here must not replace the cancellation the caller
    /// is propagating -- a job that is told it failed is retried, where one told it was
    /// cancelled is not.
    /// </para>
    /// <para>
    /// And its own budget, short. This runs while the host is stopping, and a database that
    /// has gone with it would otherwise be waited on for Npgsql's connect and command
    /// timeouts -- longer than the shutdown grace, so a clean stop becomes a kill. An
    /// attempt not handed back costs one of five.
    /// </para>
    /// </remarks>
    private async Task HandBackAttempt(Guid pendingLinkId)
    {
        using var budget = new CancellationTokenSource(HandBackTimeout);

        try
        {
            dbContext.ChangeTracker.Clear();

            if (await dbContext.Set<PendingBankLink>()
                    .FirstOrDefaultAsync(link => link.Id == pendingLinkId, budget.Token) is not { } again)
            {
                return;
            }

            again.Attempts--;
            await dbContext.SaveChangesAsync(budget.Token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Held bank link {PendingLinkId} was interrupted and its attempt could not be handed back.",
                pendingLinkId);
        }
    }

    /// <summary>
    /// Gives up on a held link that has been tried enough times: hands back whatever access
    /// it is still holding, and drops the row.
    /// </summary>
    /// <remarks>
    /// The alternative -- leaving the row where it is -- keeps a working bank access token
    /// in a table nothing surfaces and nothing ever revisits, naming an item no connection
    /// owns and nobody can see. Retiring is the one action that actually resolves that: the
    /// access goes back, the item stops being counted at the provider, and there is then
    /// nothing a row could still be useful for.
    /// <para>
    /// Retired before the row goes, not after, so a crash in between costs the row rather
    /// than the item. Where the provider will not listen the row still goes and the log
    /// line is what remains -- it names the item, which is what somebody clearing up by
    /// hand needs.
    /// </para>
    /// </remarks>
    public async Task AbandonPending(Guid pendingLinkId, CancellationToken ct = default)
    {
        var pending = await dbContext.Set<PendingBankLink>()
            .FirstOrDefaultAsync(link => link.Id == pendingLinkId, ct);

        if (pending is null)
        {
            return;
        }

        var item = pending.ItemCiphertext is { } stored ? Read(stored, pending.Id) : null;

        if (item is null)
        {
            // Never got as far as an item, or the item it got can no longer be read. Either
            // way there is no token here to hand anything back with.
            logger.LogWarning(
                "Giving up on held bank link {PendingLinkId} at {Provider}. It holds no item this deployment "
                + "can read, so there is nothing to hand back.",
                pending.Id, pending.Provider);
        }
        else if (await TryRetire(pending.Provider, item.AccessToken, item.ProviderItemId))
        {
            logger.LogWarning(
                "Gave up on held bank link {PendingLinkId} and handed item {ProviderItemId} back to {Provider}.",
                pending.Id, item.ProviderItemId, pending.Provider);
        }
        else
        {
            // The one outcome that leaves something behind, and this line is its only
            // record once the row is gone.
            logger.LogError(
                "Gave up on held bank link {PendingLinkId} and could not hand item {ProviderItemId} back to "
                + "{Provider}. It exists there, belongs to no connection here, and has to be removed by hand.",
                pending.Id, item.ProviderItemId, pending.Provider);
        }

        dbContext.Remove(pending);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Drops a held link and says why, without letting the drop itself become the failure.
    /// </summary>
    /// <remarks>
    /// Best effort on purpose: every caller is on its way out with something more important
    /// to report, and a row that survives this is picked up by the sweep, which reaches the
    /// same conclusion and drops it there.
    /// </remarks>
    private async Task Discard(PendingBankLink pending, string why, CancellationToken ct)
    {
        try
        {
            dbContext.Remove(pending);
            await dbContext.SaveChangesAsync(ct);

            logger.LogInformation("Dropped held bank link {PendingLinkId}: {Why}.", pending.Id, why);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Held bank link {PendingLinkId} could not be dropped; the sweep will get to it.", pending.Id);
        }
    }

    /// <summary>The item a held link wrote down, or null if it cannot be read back.</summary>
    private LinkedItem? Read(string ciphertext, Guid pendingLinkId)
    {
        try
        {
            return JsonSerializer.Deserialize<LinkedItem>(protector.Unprotect(ciphertext), ItemJson);
        }
        catch (Exception e) when (e is AccessTokenUnreadableException or JsonException)
        {
            logger.LogError(e,
                "The item held for bank link {PendingLinkId} cannot be read, so the link cannot be finished.",
                pendingLinkId);

            return null;
        }
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
            return Read(stored, pending.Id);
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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Expired, most likely, which is ordinary and not worth an error: the person
            // links again, and that is the one case where an item really is spent. A
            // shutdown is not one of these -- that is the host stopping, says nothing about
            // the token, and must not spend one of this row's attempts.
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

            MergeAccounts(existing, item.Accounts, dbContext);

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

        MergeAccounts(created, item.Accounts, dbContext);

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

        // The merchant stays. It is where the expense happened, which is as true after the
        // bank is gone as it was before -- and the row it points at is shared by every other
        // group that shops there, so unlinking one person's card cannot take it away.
        foreach (var transaction in filed)
        {
            transaction.BankTransaction = null;
            transaction.BankTransactionId = null;
        }

        // Told first, deleted second. A provider that refuses leaves the connection here,
        // which the person can try again; deleting first and failing to tell them would
        // leave an item nobody owns still being billed for and still sending webhooks.
        if (Connector(connection.Provider) is not { } connector)
        {
            logger.LogWarning(
                "Unlinking connection {ConnectionId} without telling {Provider}: no connector is registered for it.",
                connection.Id, connection.Provider);
        }
        else if (Readable(connection) is { } accessToken)
        {
            await Call(connection.Provider, "remove the item", () => connector.RemoveAsync(accessToken, ct));
        }
        else
        {
            // Nothing to tell them with, and that is precisely the connection somebody is
            // most likely to be removing: the two other readers of a stored token now refuse
            // to work with an unreadable one and say so, and both point here as the way out.
            // Throwing would make the way out the one thing that does not work. The item is
            // stranded at the provider either way -- an unreadable token is unreadable for
            // /item/remove as much as for a sync -- so the rows go and the log carries what
            // is left of it.
            logger.LogError(
                "Unlinking connection {ConnectionId} without telling {Provider}: its access token cannot be "
                + "read, so item {ProviderItemId} stays at the provider and has to be removed by hand.",
                connection.Id, connection.Provider, connection.ProviderItemId);
        }

        // The expenses filed from this connection's rows keep everything but the link: the
        // relationship is SetNull, so they are spending history rather than an import.
        dbContext.Remove(connection);
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task<BankConnection> Refresh(Guid id, CancellationToken ct = default)
    {
        var connection = await Existing(id, ct);
        var connector = Required(connection.Provider);

        var token = Readable(connection)
            ?? throw new ConflictException(ErrorCodes.BankConnectionNeedsAttention,
                "This bank's stored access cannot be read any more. Link it again to repair it.");

        IReadOnlyList<ImportedAccount> accounts;

        try
        {
            accounts = await Call(connection.Provider, "read the accounts",
                () => connector.AccountsAsync(token, ct));
        }
        catch (BadGatewayException e) when (e.InnerException is BankSyncException
                                            { Kind: BankSyncFailure.LoginRequired })
        {
            // Not the provider having a bad minute, which is what a bad gateway says and
            // what the person would then be told: this item needs them, and the app already
            // has a way of saying so. Marking it is also what puts "Sign in again" on the
            // card, so refusing without marking would leave them nothing to press.
            connection.Status = BankConnectionStatus.LoginRequired;
            await dbContext.SaveChangesAsync(ct);

            // The cause is not carried: ConflictException takes none, and both the connector
            // and Call have already logged the provider's own code by the time this runs.
            throw new ConflictException(ErrorCodes.BankConnectionNeedsAttention,
                "This bank wants you to sign in again before it will say what accounts it has.");
        }

        MergeAccounts(connection, accounts, dbContext);

        // Reading the accounts at all proves the token works, so whatever the connection was
        // marked as before, it is working now.
        connection.Status = BankConnectionStatus.Active;

        // Less certain than the line above, and deliberately so. A refresh is what the card
        // does at the end of update mode, and update mode is what renews a consent -- but
        // the provider sends nothing to confirm a renewal, so this is the only signal there
        // is. A refresh run on its own therefore clears a warning it did not resolve. The
        // cost is bounded: the consent still lapses on its own schedule and arrives as
        // LoginRequired, which is the state this was warning about in advance.
        connection.SignInExpiring = false;

        // A refresh is how update mode ends, so this is the moment the missing account was
        // either shared or not. Cleared either way: the next sync to meet a row on an
        // account still unknown puts it straight back.
        connection.AccountsNotShared = false;

        await dbContext.SaveChangesAsync(ct);

        await jobs.DispatchAsync(new SyncBankConnection(connection.Id), ct);

        return connection;
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
                        account.Id, account.Name, account.Mask, account.Type, account.Subtype, account.Currency,
                        account.AccessRevoked))
            ],
            connection.AccountsNotShared,
            connection.SignInExpiring);

    private IQueryable<BankConnection> Owned() =>
        dbContext.Set<BankConnection>().Where(connection => connection.UserId == userContext.User.Id);

    private async Task<BankConnection> Existing(Guid id, CancellationToken ct) =>
        await Owned().Include(connection => connection.Accounts)
            .FirstOrDefaultAsync(connection => connection.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.BankConnectionNotFound, "Bank connection not found.");

    private IBankConnector? Connector(string provider) => services.GetKeyedService<IBankConnector>(provider);

    /// <summary>
    /// A connection's access token, or null when the key ring can no longer open it.
    /// </summary>
    /// <remarks>
    /// Every reader of a stored token has to answer this question, and each one answers it
    /// differently -- a sync marks the connection and stops, a repair refuses and says why,
    /// an unlink carries on without telling the provider. What they share is that none of
    /// them may let it out as an unhandled exception, which is what it was.
    /// </remarks>
    private string? Readable(BankConnection connection)
    {
        try
        {
            return protector.Unprotect(connection.AccessTokenCiphertext);
        }
        catch (AccessTokenUnreadableException)
        {
            return null;
        }
    }

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
    /// item id. What does survive a re-link is the mask -- the last few digits the bank
    /// itself shows the person -- so that is what is compared.
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

    /// <summary>
    /// Whether a stored account and a freshly reported one are the same account at the bank.
    /// </summary>
    /// <remarks>
    /// The mask and nothing else. It is the bank's own last few digits, it is the same
    /// whichever item asks for them, and it is the only thing on offer that actually
    /// identifies an account.
    /// <para>
    /// Where an institution reports no mask this answers no, and the two are treated as
    /// different accounts. The tempting fallback is the name, and it is the wrong trade: the
    /// names on offer are "Checking", "Savings", "Current Account", which two entirely
    /// separate logins at one bank will happily both report. Being wrong in that direction
    /// is not a missed tidy-up -- <see cref="Adopt"/> would move the first connection onto
    /// the second login's item, re-point its accounts at accounts they are not, and then
    /// remove the first item at the provider. A missed duplicate costs one item. A false one
    /// costs somebody a working bank connection and files their history against the wrong
    /// account.
    /// </para>
    /// </remarks>
    private static bool SameAccount(LinkedAccount stored, ImportedAccount incoming) =>
        stored.Type == incoming.Type
        && !string.IsNullOrWhiteSpace(stored.Mask)
        && !string.IsNullOrWhiteSpace(incoming.Mask)
        && stored.Mask == incoming.Mask;

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

        // Which means the whole history comes back -- under new transaction ids, because a
        // provider mints those per item too. Flagged, so the next page run recognises what
        // it already has by what the rows look like rather than by an id that changed.
        // Without it every filed expense of the last however-many months reappears in the
        // inbox as something new, and the duplicate check cannot catch a single one.
        connection.AccountsRekeyed = true;

        AdoptAccounts(connection, item.Accounts, dbContext);

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
    /// the first. What survives a re-link is the mask the bank shows the person, so that is
    /// what is matched on instead -- and an account with no mask on either side matches
    /// nothing and is simply added, which is the same caution <see cref="SameAccount"/>
    /// takes for the same reason.
    /// <para>
    /// And nothing is retired here, which is the other half of that caution. An account the
    /// mask did not recognise has not been withdrawn -- it has been added again under a new
    /// id, beside the row that holds its history -- so saying "access was withdrawn" of it
    /// would be the same false statement this application is trying to stop making, pointed
    /// the other way. Only a match on the provider's own id is evidence of absence.
    /// </para>
    /// </remarks>
    private static void AdoptAccounts(
        BankConnection connection,
        IReadOnlyList<ImportedAccount> accounts,
        AppDbContext dbContext) =>
        Reconcile(connection, accounts, SameAccount, dbContext, retireUnreported: false);

    /// <summary>
    /// Keeps the accounts the provider reports, adding new ones and refreshing the details of
    /// the ones already here. Nothing is removed: rows point at accounts, and an account the
    /// provider stopped listing still explains where last month's coffee came from.
    /// </summary>
    internal static void MergeAccounts(
        BankConnection connection,
        IReadOnlyList<ImportedAccount> accounts,
        AppDbContext dbContext) =>
        Reconcile(connection, accounts,
            (stored, incoming) => stored.ProviderAccountId == incoming.ProviderAccountId, dbContext,
            retireUnreported: true);

    /// <summary>
    /// The body both of those share: every reported account is either recognised by
    /// <paramref name="recognises"/> and brought up to date, or added.
    /// </summary>
    /// <remarks>
    /// A recognised account has every field written, the two keys included. That is what each
    /// caller already did: whichever key it matched on it left alone, and writing a value a
    /// match has just proved equal changes nothing -- so one body is the same work as two,
    /// without the second copy to keep in step.
    /// <para>
    /// A stored account is claimed by at most one reported one. Neither key should ever
    /// match twice -- ids are unique within an item and masks within a login -- but the
    /// failure if one did is silent and bad: the second write would land on the row the
    /// first had already taken, and the account it belonged to would end up with no row at
    /// all rather than a new one.
    /// </para>
    /// </remarks>
    /// <param name="retireUnreported">
    /// Whether an account the provider did not report should be marked withdrawn. Only when
    /// <paramref name="recognises"/> matches on the provider's own account id: under any
    /// looser key a stored account can go unmatched while the bank is still perfectly
    /// happy to hand it over, and marking that one is worse than missing a real withdrawal.
    /// </param>
    private static void Reconcile(
        BankConnection connection,
        IReadOnlyList<ImportedAccount> accounts,
        Func<LinkedAccount, ImportedAccount, bool> recognises,
        AppDbContext dbContext,
        bool retireUnreported)
    {
        var claimed = new HashSet<LinkedAccount>();

        // Snapshotted before the loop adds to it. A newly created account is not claimed
        // by anything -- it is what did the claiming -- so reading the collection
        // afterwards would find it unmatched and retire it the instant it arrived.
        var before = connection.Accounts.ToList();

        foreach (var account in accounts)
        {
            var stored = connection.Accounts
                .FirstOrDefault(candidate => !claimed.Contains(candidate) && recognises(candidate, account));

            if (stored is null)
            {
                var created = new LinkedAccount
                {
                    BankConnectionId = connection.Id,
                    ProviderAccountId = account.ProviderAccountId,
                    Name = account.Name,
                    Mask = account.Mask,
                    Type = account.Type,
                    Subtype = account.Subtype,
                    Currency = account.Currency
                };

                connection.Accounts.Add(created);

                // Both, and the Add is the half that matters. Entity hands every instance an
                // Id at construction, so a new account discovered through the navigation of
                // an already-stored connection has a key that is not the default -- and EF
                // reads that as "already in the store", stages an UPDATE, and fails the save
                // with a concurrency exception when it turns out not to be. Only a graph
                // hanging off a connection that is itself being added escapes it, which is
                // why linking a bank for the first time never showed this.
                dbContext.Add(created);

                continue;
            }

            claimed.Add(stored);

            stored.ProviderAccountId = account.ProviderAccountId;
            stored.Name = account.Name;
            stored.Mask = account.Mask;
            stored.Type = account.Type;
            stored.Subtype = account.Subtype;
            stored.Currency = account.Currency;

            // The provider is handing it over again, so whatever was withdrawn has been
            // shared back. Nothing else clears this: a revoked account stops being reported
            // at all, so being here is the whole of the evidence.
            stored.AccessRevoked = false;
        }

        // And the other direction. An account the provider has stopped reporting is one
        // somebody unshared -- in update mode, or at the bank -- and it is kept rather than
        // deleted for the reason above this method: rows point at it, and it still explains
        // where last month's coffee came from.
        //
        // Kept is not the same as healthy, though, and that was the gap. Listed beside the
        // accounts that are still importing, with nothing to tell them apart, it goes on
        // looking connected for ever while nothing arrives for it again -- the same silence
        // issue 233 was about, from the opposite end.
        //
        // Only where the match was on the provider's account id, though. See the parameter.
        if (!retireUnreported)
            return;

        foreach (var stored in before.Where(candidate => !claimed.Contains(candidate)))
        {
            stored.AccessRevoked = true;
        }
    }
}

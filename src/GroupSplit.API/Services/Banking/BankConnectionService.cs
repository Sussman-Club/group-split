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
                // here. Nothing good comes of joining those two together.
                logger.LogWarning("Bank item {ProviderItemId} is already linked to another account.", item.ProviderItemId);
                throw new NotFoundException(ErrorCodes.BankConnectionNotFound, "That bank connection is not available.");
            }

            existing.AccessTokenCiphertext = protector.Protect(item.AccessToken);
            existing.InstitutionName = item.InstitutionName;
            existing.Status = BankConnectionStatus.Active;

            MergeAccounts(existing, item.Accounts);

            await dbContext.SaveChangesAsync(ct);
            await jobs.DispatchAsync(new SyncBankConnection(existing.Id), ct);

            return existing;
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
        await dbContext.SaveChangesAsync(ct);

        await jobs.DispatchAsync(new SyncBankConnection(created.Id), ct);

        return created;
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

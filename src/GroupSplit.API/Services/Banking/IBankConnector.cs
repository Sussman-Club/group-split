namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Everything the app asks of a bank data provider, in the app's terms.
/// </summary>
/// <remarks>
/// The seam the Phase 3 plan describes. Above it -- the sync engine, the inbox, the
/// endpoints, the client -- nothing knows a provider's vocabulary; below it, one class per
/// provider knows nothing but its own. Every aggregator, and a CSV or OFX file, yields the
/// same shape: an account with rows that have an external id, a date, an amount, a merchant
/// and possibly a pending state. That shape is the records in this file.
/// <para>
/// Connectors are keyed services, resolved by <see cref="Provider"/>, which is also the
/// value stored on <c>BankConnection.Provider</c> and the segment the webhook route
/// carries. Adding a provider is a class and a registration.
/// </para>
/// </remarks>
public interface IBankConnector
{
    /// <summary>
    /// The key this connector is registered under: lower-case, short, stable. <c>"plaid"</c>.
    /// </summary>
    string Provider { get; }

    /// <summary>
    /// A short-lived token the client hands to the provider's own UI so a person can pick
    /// a bank and sign in. With <see cref="LinkSessionRequest.AccessToken"/> set, the same
    /// UI opens in update mode on an existing connection instead.
    /// </summary>
    Task<LinkSession> CreateLinkSessionAsync(LinkSessionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Turns what the provider's UI handed back into a durable access token, and reads the
    /// institution and its accounts while the connection is fresh.
    /// </summary>
    Task<LinkedItem> ExchangeAsync(string publicToken, CancellationToken ct = default);

    /// <summary>
    /// One page of changes since <paramref name="cursor"/>, or from the beginning when it
    /// is null. Callers loop until <see cref="SyncPage.HasMore"/> is false.
    /// </summary>
    /// <exception cref="BankSyncException">
    /// The one exception a connector is allowed to throw: for the token no longer working,
    /// for the provider asking that the page run start over, or for something transient.
    /// Anything else is a bug and propagates as one.
    /// </exception>
    Task<SyncPage> SyncAsync(string accessToken, string? cursor, CancellationToken ct = default);

    /// <summary>
    /// Tells the provider the connection is over, so it stops billing and stops sending
    /// webhooks for it.
    /// </summary>
    Task RemoveAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="body"/> genuinely came from the provider, by whatever
    /// scheme the provider signs with. A false is a 401 and nothing else happens.
    /// </summary>
    Task<bool> VerifyWebhookAsync(IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default);

    /// <summary>
    /// What a verified webhook body means, in the app's terms.
    /// </summary>
    WebhookEvent ParseWebhook(string body);
}

/// <summary>
/// What a link session is for.
/// </summary>
/// <param name="ClientUserId">The provider's handle for the person: our user id.</param>
/// <param name="WebhookUrl">Where the provider should send webhooks for the resulting connection.</param>
/// <param name="RedirectUri">Where a bank's own sign-in page should send the person back to, when one is involved.</param>
/// <param name="AccessToken">Set to open update mode on an existing connection rather than link a new one.</param>
public sealed record LinkSessionRequest(string ClientUserId, string? WebhookUrl, string? RedirectUri, string? AccessToken);

public sealed record LinkSession(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// A connection the provider has just made durable.
/// </summary>
public sealed record LinkedItem(
    string AccessToken,
    string ProviderItemId,
    string InstitutionName,
    IReadOnlyList<ImportedAccount> Accounts);

public sealed record ImportedAccount(
    string ProviderAccountId,
    string Name,
    string? Mask,
    string Type,
    string? Subtype,
    string Currency);

/// <summary>
/// One row as a provider reports it, translated into the app's terms.
/// </summary>
/// <param name="Amount">
/// Positive when money left the account. This is the app's convention, and translating
/// the provider's into it is the connector's job; nothing above the seam re-signs a number.
/// </param>
/// <param name="ReplacesProviderTransactionId">
/// For a posted row, the pending row it settles, when the provider says which.
/// </param>
/// <param name="RawJson">The provider's row, verbatim, for whatever is wanted later.</param>
public sealed record ImportedTransaction(
    string ProviderAccountId,
    string ProviderTransactionId,
    DateOnly Date,
    decimal Amount,
    string Currency,
    string Description,
    string? MerchantName,
    string? ProviderCategory,
    string? ProviderCategoryDetailed,
    DateOnly? AuthorizedDate,
    string? PaymentChannel,
    string? City,
    string? LogoUrl,
    /// <summary>
    /// A mark for the kind of thing this was, where the provider has one and has no logo
    /// for the merchant itself. Plaid populates this on every row and populates
    /// <see cref="LogoUrl"/> only for merchants it recognises, so this is what most rows
    /// actually have to show.
    /// </summary>
    string? CategoryIconUrl,
    bool Pending,
    string? ReplacesProviderTransactionId,
    string RawJson);

public sealed record RemovedTransaction(string ProviderAccountId, string ProviderTransactionId);

/// <summary>
/// One page of a sync. <paramref name="NextCursor"/> is the provider's, opaque, and is
/// stored only once <paramref name="HasMore"/> has come back false.
/// </summary>
public sealed record SyncPage(
    IReadOnlyList<ImportedTransaction> Added,
    IReadOnlyList<ImportedTransaction> Modified,
    IReadOnlyList<RemovedTransaction> Removed,
    string NextCursor,
    bool HasMore);

/// <summary>
/// What a webhook meant. The connector reads the provider's codes; the route reads these.
/// </summary>
public abstract record WebhookEvent(string ProviderItemId);

/// <summary>The provider has changes; run a sync.</summary>
public sealed record SyncUpdatesAvailable(string ProviderItemId) : WebhookEvent(ProviderItemId);

/// <summary>The token stopped working and the person has to sign in at the bank again.</summary>
public sealed record LoginRequired(string ProviderItemId) : WebhookEvent(ProviderItemId);

/// <summary>They did; the connection works again.</summary>
public sealed record LoginRepaired(string ProviderItemId) : WebhookEvent(ProviderItemId);

/// <summary>The person withdrew access at the bank's end.</summary>
public sealed record PermissionRevoked(string ProviderItemId) : WebhookEvent(ProviderItemId);

/// <summary>Something the app does not act on. Acknowledged and logged, nothing more.</summary>
public sealed record UnhandledWebhook(string ProviderItemId, string Code) : WebhookEvent(ProviderItemId);

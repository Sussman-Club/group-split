using System.Text.Json;
using Going.Plaid;
using Going.Plaid.Accounts;
using Going.Plaid.Entity;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Going.Plaid.Transactions;
using GroupSplit.Data.Entities;
using Microsoft.Extensions.Options;
using PlaidTransaction = Going.Plaid.Entity.Transaction;

namespace GroupSplit.API.Services.Banking.Plaid;

/// <summary>
/// The only code in the application that speaks Plaid.
/// </summary>
/// <remarks>
/// Everything above the seam sees the records in <see cref="IBankConnector"/>; everything
/// Plaid-shaped stops here. That is what makes a second aggregator a second class of this
/// size rather than a change to the sync engine, the inbox or the UI.
/// <para>
/// The access token is set on each request rather than on the shared client, whose own
/// <c>AccessToken</c> is never touched. Going.Plaid fills a request's credentials in only
/// where they are blank, so two syncs running at once cannot end up using each other's
/// token -- which a mutable property on a singleton client would have allowed.
/// </para>
/// <para>
/// Plaid answers failures in the response rather than by throwing, so every call checks and
/// translates. The three that the sync engine can do something about become a
/// <see cref="BankSyncException"/>; everything else is a fault, and reads like one.
/// </para>
/// </remarks>
public sealed class PlaidConnector(
    PlaidClient plaid,
    PlaidWebhookVerifier verifier,
    IOptions<PlaidConnectorOptions> options,
    ILogger<PlaidConnector> logger) : IBankConnector
{
    public const string Name = "plaid";

    /// <summary>Plaid asks for at most 500 updates per page.</summary>
    private const int PageSize = 500;

    private PlaidConnectorOptions Options => options.Value;

    public string Provider => Name;

    public async Task<LinkSession> CreateLinkSessionAsync(LinkSessionRequest request, CancellationToken ct = default)
    {
        var updating = !string.IsNullOrWhiteSpace(request.AccessToken);

        var response = await plaid.LinkTokenCreateAsync(new LinkTokenCreateRequest
        {
            ClientName = Options.ClientName,
            Language = Language.English,
            CountryCodes = Options.CountryCodes.Select(ToCountryCode).ToArray(),
            User = new LinkTokenCreateRequestUser { ClientUserId = request.ClientUserId },
            // Update mode repairs an existing item and must name no products: it is not
            // asking for anything new, only for the sign-in to be redone.
            Products = updating ? [] : [Products.Transactions],
            Transactions = updating ? null : new LinkTokenTransactions { DaysRequested = Options.DaysRequested },
            AccessToken = request.AccessToken,
            Webhook = request.WebhookUrl,
            RedirectUri = request.RedirectUri ?? Options.RedirectUri
        });

        Ensure(response, "create a link token");

        return new LinkSession(
            response.LinkToken,
            response.Expiration == default ? DateTimeOffset.UtcNow.AddMinutes(30) : response.Expiration);
    }

    public async Task<LinkedItem> ExchangeAsync(string publicToken, CancellationToken ct = default)
    {
        var exchange = await plaid.ItemPublicTokenExchangeAsync(new ItemPublicTokenExchangeRequest
        {
            PublicToken = publicToken
        });

        Ensure(exchange, "exchange the public token");

        // Fetched while the connection is new, because this is the one moment the accounts
        // are certain to be reachable and the institution's name is worth keeping.
        var accounts = await plaid.AccountsGetAsync(new AccountsGetRequest { AccessToken = exchange.AccessToken });

        Ensure(accounts, "read the accounts");

        return new LinkedItem(
            exchange.AccessToken,
            exchange.ItemId,
            accounts.Item?.InstitutionName ?? "Your bank",
            accounts.Accounts.Select(ToAccount).ToList());
    }

    public async Task<SyncPage> SyncAsync(string accessToken, string? cursor, CancellationToken ct = default)
    {
        var response = await plaid.TransactionsSyncAsync(new TransactionsSyncRequest
        {
            AccessToken = accessToken,
            Cursor = cursor,
            Count = PageSize
        });

        Ensure(response, "read a page of transactions");

        return new SyncPage(
            Rows(response.Added),
            Rows(response.Modified),
            response.Removed.Select(removed => new RemovedTransaction(removed.AccountId, removed.TransactionId)).ToList(),
            response.NextCursor,
            response.HasMore);
    }

    public async Task RemoveAsync(string accessToken, CancellationToken ct = default)
    {
        var response = await plaid.ItemRemoveAsync(new ItemRemoveRequest { AccessToken = accessToken });

        Ensure(response, "remove the item");
    }

    public Task<bool> VerifyWebhookAsync(IReadOnlyDictionary<string, string> headers, string body,
        CancellationToken ct = default) =>
        verifier.VerifyAsync(headers, body, ct);

    public WebhookEvent ParseWebhook(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var itemId = Text(root, "item_id") ?? string.Empty;
        var type = Text(root, "webhook_type");
        var code = Text(root, "webhook_code");

        return (type, code) switch
        {
            ("TRANSACTIONS", "SYNC_UPDATES_AVAILABLE") => new SyncUpdatesAvailable(itemId),
            ("ITEM", "LOGIN_REPAIRED") => new LoginRepaired(itemId),
            ("ITEM", "USER_PERMISSION_REVOKED") => new PermissionRevoked(itemId),
            ("ITEM", "ERROR") when ErrorCode(root) == "ITEM_LOGIN_REQUIRED" => new LoginRequired(itemId),
            _ => new UnhandledWebhook(itemId, $"{type}/{code}")
        };
    }

    /// <summary>
    /// Plaid's rows, in the app's terms. Rows missing the few things that make a row usable
    /// are dropped with a warning rather than imported as zeroes on the epoch.
    /// </summary>
    private List<ImportedTransaction> Rows(IReadOnlyList<PlaidTransaction> transactions)
    {
        var rows = new List<ImportedTransaction>(transactions.Count);

        foreach (var transaction in transactions)
        {
            if (transaction.TransactionId is not { Length: > 0 } id
                || transaction.AccountId is not { Length: > 0 } accountId
                || transaction.Date is not { } date
                || transaction.Amount is not { } amount)
            {
                logger.LogWarning("Skipping a Plaid transaction with no id, account, date or amount.");
                continue;
            }

            rows.Add(new ImportedTransaction(
                accountId,
                id,
                date,
                // Plaid is positive for money leaving the account, and so is an expense.
                // The two conventions agree, so the number is carried across untouched --
                // and this comment is the only place that has to say so.
                amount,
                Currency(transaction.IsoCurrencyCode, transaction.UnofficialCurrencyCode),
                transaction.Name ?? string.Empty,
                transaction.MerchantName,
                transaction.PersonalFinanceCategory?.Primary,
                transaction.Pending ?? false,
                transaction.PendingTransactionId,
                JsonSerializer.Serialize(transaction, SerializerOptions)));
        }

        return rows;
    }

    private static ImportedAccount ToAccount(Account account) =>
        new(account.AccountId,
            account.Name,
            account.Mask,
            account.Type.ToString().ToLowerInvariant(),
            account.Subtype?.ToString().ToLowerInvariant(),
            Currency(account.Balances?.IsoCurrencyCode, account.Balances?.UnofficialCurrencyCode));

    /// <summary>
    /// ISO 4217 if Plaid gave one. The column is three fixed characters, and an unofficial
    /// code -- a crypto ticker, say -- may be anything, so anything that is not three
    /// characters falls back rather than being truncated into a different currency.
    /// </summary>
    private static string Currency(string? iso, string? unofficial)
    {
        var code = string.IsNullOrWhiteSpace(iso) ? unofficial : iso;

        return code is { Length: Currencies.CodeLength } ? code.ToUpperInvariant() : Currencies.Default;
    }

    private static CountryCode ToCountryCode(string code) =>
        Enum.TryParse<CountryCode>(code, ignoreCase: true, out var parsed) ? parsed : CountryCode.Us;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ErrorCode(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "error_code")
            : null;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Turns a failed response into the exception the caller can act on.
    /// </summary>
    /// <remarks>
    /// Two groups of Plaid's codes mean something specific here. The ones saying the token
    /// no longer works end the sync and mark the connection, so it stops being retried and
    /// starts asking the person to do something.
    /// <c>TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION</c> is Plaid saying the data moved
    /// underneath a page run, and its own guidance is to start that run again from the
    /// cursor it began with.
    /// <para>
    /// Everything else is transient, including codes this was never taught. That is a
    /// deliberate choice over failing loudly: Plaid's error space grows, and a connector
    /// that died on a code added last week would stop somebody's bank syncing over a name
    /// it did not recognise. Nothing is lost by trying again, because a page run that
    /// failed did not move the cursor, and the daily sweep bounds how often it happens.
    /// The warning below is what makes it visible in the meantime.
    /// </para>
    /// </remarks>
    private void Ensure(ResponseBase response, string what)
    {
        if (response.IsSuccessStatusCode)
            return;

        var error = response.Error;
        var code = error?.ErrorCode ?? "UNKNOWN";

        logger.LogWarning("Plaid could not {What}: {ErrorCode} ({ErrorType}). Request {RequestId}.",
            what, code, error?.ErrorType, response.RequestId);

        var kind = code switch
        {
            // The item needs a person, not a retry. Marking the connection is what puts it
            // in front of one.
            "ITEM_LOGIN_REQUIRED" => BankSyncFailure.LoginRequired,
            "ITEM_NOT_FOUND" => BankSyncFailure.LoginRequired,
            "INVALID_ACCESS_TOKEN" => BankSyncFailure.LoginRequired,
            "ITEM_NOT_SUPPORTED" => BankSyncFailure.LoginRequired,

            "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION" => BankSyncFailure.RestartFromCursor,

            _ => BankSyncFailure.Transient
        };

        throw new BankSyncException(kind, $"Plaid could not {what}: {code}.");
    }
}

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
            Update = updating ? new LinkTokenCreateRequestUpdate { AccountSelectionEnabled = true } : null,
            Webhook = request.WebhookUrl,
            RedirectUri = Address(request.RedirectUri) ?? Address(Options.RedirectUri)
        });

        Ensure(response, "create a link token");

        return new LinkSession(
            response.LinkToken,
            response.Expiration == default ? DateTimeOffset.UtcNow.AddMinutes(30) : response.Expiration);
    }

    /// <summary>
    /// An address, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Blank is how "not configured" arrives. Every optional parameter in the AppHost
    /// resolves to an empty string when nobody sets it, and the rest of this application
    /// already reads blank as absent -- an empty client id is what switches bank sync off.
    /// Plaid draws no such distinction: it refuses <c>redirect_uri: ""</c> with
    /// INVALID_FIELD, the same as it refuses an address nobody registered. Passing one
    /// through would turn "this deployment has no redirect" into every link failing.
    /// </remarks>
    private static string? Address(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task<IReadOnlyList<ImportedAccount>> AccountsAsync(string accessToken, CancellationToken ct = default)
    {
        var accounts = await plaid.AccountsGetAsync(new AccountsGetRequest { AccessToken = accessToken });

        Ensure(accounts, "read the accounts");

        return accounts.Accounts.Select(ToAccount).ToList();
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
            ("ITEM", "NEW_ACCOUNTS_AVAILABLE") => new NewAccountsAvailable(itemId),
            // Both carry the date they are about, and one carries Plaid's own reason.
            // Reading them is the difference between "soon" and a day somebody can act on.
            ("ITEM", "PENDING_DISCONNECT") => new SignInWillExpire(
                itemId,
                Text(root, "reason") ?? "the institution is being migrated",
                When(root, "disconnect_time")),

            ("ITEM", "PENDING_EXPIRATION") => new SignInWillExpire(
                itemId,
                "the consent is about to expire",
                When(root, "consent_expiration_time")),
            ("ITEM", "USER_ACCOUNT_REVOKED") => Text(root, "account_id") is { Length: > 0 } account
                ? new AccountAccessRevoked(itemId, account)
                : new UnhandledWebhook(itemId, $"{type}/{code} (no account_id)"),
            ("ITEM", "LOGIN_REPAIRED") => new LoginRepaired(itemId),
            ("ITEM", "USER_PERMISSION_REVOKED") => new PermissionRevoked(itemId),

            // The same codes Ensure treats as needing a person, because an item error
            // arriving by webhook means exactly what one arriving from a failed call does.
            ("ITEM", "ERROR") when NeedsSignIn(ErrorCode(root)) => new LoginRequired(itemId),
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
        var dropped = new List<string>();

        foreach (var transaction in transactions)
        {
            if (transaction.TransactionId is not { Length: > 0 } id
                || transaction.AccountId is not { Length: > 0 } accountId
                || transaction.Date is not { } date
                || transaction.Amount is not { } amount)
            {
                // Named and counted. A warning that says only that something was dropped
                // cannot be chased: there is no way to tell one bad row from a page of them,
                // or to ask Plaid about the one that went missing.
                dropped.Add(Missing(transaction));
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
                // Plaid marks this legacy in favour of merchant_name, which is a different
                // thing and is read below. This is the raw line the bank wrote, which the
                // sync endpoint always returns and nothing else provides.
#pragma warning disable CS0612
                transaction.Name ?? string.Empty,
#pragma warning restore CS0612
                transaction.MerchantName,
                transaction.PersonalFinanceCategory?.Primary,
                transaction.PersonalFinanceCategory?.Detailed,
                // What the person remembers, where Plaid knows it. Date is the posting
                // date for a settled row and can be days later.
                transaction.AuthorizedDate,
                transaction.PaymentChannel?.ToString().ToLowerInvariant(),
                transaction.Location?.City,
                transaction.LogoUrl,
                // Sent on every row, unlike the merchant's own logo -- Plaid fills that in
                // only for merchants it recognises, and never in the sandbox. Without this
                // an imported row has nothing to show but two initials.
                transaction.PersonalFinanceCategoryIconUrl,
                transaction.Pending ?? false,
                transaction.PendingTransactionId,
                JsonSerializer.Serialize(transaction, SerializerOptions)));
        }

        if (dropped.Count > 0)
        {
            logger.LogWarning(
                "Skipped {Count} of {Total} Plaid transactions that were missing an id, account, date or "
                + "amount: {Dropped}.", dropped.Count, transactions.Count, string.Join("; ", dropped));
        }

        return rows;
    }

    /// <summary>What a dropped row was missing, and whatever of it can be quoted back.</summary>
    private static string Missing(PlaidTransaction transaction)
    {
        var absent = new List<string>(4);

        if (transaction.TransactionId is not { Length: > 0 }) absent.Add("id");
        if (transaction.AccountId is not { Length: > 0 }) absent.Add("account");
        if (transaction.Date is null) absent.Add("date");
        if (transaction.Amount is null) absent.Add("amount");

        var known = transaction.TransactionId is { Length: > 0 } id
            ? id
            : transaction.AccountId is { Length: > 0 } account ? $"account {account}" : "nothing to name it by";

        return $"{known} (no {string.Join(", ", absent)})";
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

    /// <summary>An ISO 8601 instant the provider sent, or null if it sent nothing usable.</summary>
    private static DateTimeOffset? When(JsonElement root, string name) =>
        Text(root, name) is { } text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;

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
    /// <summary>
    /// Plaid's codes that mean the item needs a person rather than a retry. Shared by the
    /// two places that meet them -- a failed call and an <c>ITEM: ERROR</c> webhook -- which
    /// had drifted to one code on the webhook side and four on the other.
    /// </summary>
    private static readonly HashSet<string> SignInCodes = new(StringComparer.Ordinal)
    {
        "ITEM_LOGIN_REQUIRED",
        "ITEM_NOT_FOUND",
        "INVALID_ACCESS_TOKEN",
        "ITEM_NOT_SUPPORTED"
    };

    private static bool NeedsSignIn(string? code) => code is not null && SignInCodes.Contains(code);

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
            _ when NeedsSignIn(code) => BankSyncFailure.LoginRequired,

            "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION" => BankSyncFailure.RestartFromCursor,

            _ => BankSyncFailure.Transient
        };

        throw new BankSyncException(kind, $"Plaid could not {what}: {code}.");
    }
}

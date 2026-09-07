using System.Net;
using System.Text.Json;
using GroupSplit.API.Services.Banking;
using GroupSplit.API.Services.Banking.Plaid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Test.Banking.Plaid;

/// <summary>
/// The connector on its own, against recorded Plaid payloads: what its vocabulary becomes
/// in the app's, and which of its failures the sync engine is told it can do something
/// about.
/// </summary>
public class PlaidConnectorTest
{
    private const string SyncPath = "/transactions/sync";
    private const string AccountsPath = "/accounts/get";
    private const string ExchangePath = "/item/public_token/exchange";
    private const string LinkTokenPath = "/link/token/create";

    private readonly PlaidTestServer _plaid = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlaidConnector Connector(PlaidConnectorOptions? options = null) =>
        new(_plaid.Client(),
            new PlaidWebhookVerifier(_plaid.Client(), TimeProvider.System, NullLogger<PlaidWebhookVerifier>.Instance),
            Options.Create(options ?? new PlaidConnectorOptions()),
            NullLogger<PlaidConnector>.Instance);

    [Fact]
    public async Task A_page_of_transactions_arrives_in_the_apps_own_terms()
    {
        _plaid.AnswerWith(SyncPath, "transactions-sync-page1.json");

        var page = await Connector().SyncAsync("access-token", cursor: null, Ct);

        Assert.Equal("cursor-page-1", page.NextCursor);
        Assert.True(page.HasMore);
        Assert.Equal(2, page.Added.Count);

        var coffee = page.Added.Single(row => row.ProviderTransactionId == "txn-coffee-pending");

        Assert.Equal("acc-1", coffee.ProviderAccountId);
        Assert.Equal(new DateOnly(2026, 9, 1), coffee.Date);
        Assert.Equal("USD", coffee.Currency);
        Assert.Equal("SQ *BLUE BOTTLE", coffee.Description);
        Assert.Equal("Blue Bottle Coffee", coffee.MerchantName);
        Assert.Equal("FOOD_AND_DRINK", coffee.ProviderCategory);
        Assert.True(coffee.Pending);
        Assert.Null(coffee.ReplacesProviderTransactionId);
    }

    [Fact]
    public async Task Money_out_stays_positive_and_money_in_stays_negative()
    {
        _plaid.AnswerWith(SyncPath, "transactions-sync-page1.json");

        var page = await Connector().SyncAsync("access-token", cursor: null, Ct);

        // Plaid is positive for money leaving the account and so is an expense, so the
        // number crosses the seam untouched. A refund stays negative and becomes a credit,
        // which the inbox will refuse to file.
        Assert.Equal(4.33m, page.Added.Single(row => row.ProviderTransactionId == "txn-coffee-pending").Amount);
        Assert.Equal(-19.5m, page.Added.Single(row => row.ProviderTransactionId == "txn-refund").Amount);
    }

    [Fact]
    public async Task A_posted_row_carries_the_pending_row_it_settles()
    {
        _plaid.AnswerWith(SyncPath, "transactions-sync-page2.json");

        var page = await Connector().SyncAsync("access-token", "cursor-page-1", Ct);

        var posted = Assert.Single(page.Added);

        Assert.Equal("txn-coffee-pending", posted.ReplacesProviderTransactionId);
        Assert.False(posted.Pending);
        Assert.False(page.HasMore);

        var modified = Assert.Single(page.Modified);
        Assert.Equal("txn-groceries", modified.ProviderTransactionId);

        var removed = Assert.Single(page.Removed);
        Assert.Equal("txn-coffee-pending", removed.ProviderTransactionId);
        Assert.Equal("acc-1", removed.ProviderAccountId);
    }

    [Fact]
    public async Task The_cursor_is_sent_back_as_it_was_given()
    {
        _plaid.AnswerWith(SyncPath, "transactions-sync-page1.json");
        _plaid.AnswerWith(SyncPath, "transactions-sync-page2.json");

        await Connector().SyncAsync("access-token", cursor: null, Ct);
        await Connector().SyncAsync("access-token", "cursor-page-1", Ct);

        var second = JsonDocument.Parse(_plaid.Requests[1].Body).RootElement;

        Assert.Equal("cursor-page-1", second.GetProperty("cursor").GetString());
        // The token travels on the request, never on the shared client, so two syncs at
        // once cannot end up using each other's.
        Assert.Equal("access-token", second.GetProperty("access_token").GetString());
    }

    [Fact]
    public async Task The_whole_provider_row_is_kept_for_whatever_is_wanted_later()
    {
        _plaid.AnswerWith(SyncPath, "transactions-sync-page1.json");

        var page = await Connector().SyncAsync("access-token", cursor: null, Ct);
        var raw = JsonDocument.Parse(page.Added[0].RawJson).RootElement;

        // Not a column, and not lost either: the detailed category is in the payload for the
        // day somebody wants it.
        Assert.Equal("FOOD_AND_DRINK_COFFEE",
            raw.GetProperty("personal_finance_category").GetProperty("detailed").GetString());

        // Stored under Plaid's own names, so a row read out of the database years later
        // still matches Plaid's documentation rather than this application's casing.
        Assert.True(raw.TryGetProperty("transaction_id", out _));
        Assert.True(raw.TryGetProperty("merchant_name", out _));
    }

    [Theory]
    [InlineData("error-item-login-required.json", BankSyncFailure.LoginRequired)]
    [InlineData("error-mutation-during-pagination.json", BankSyncFailure.RestartFromCursor)]
    [InlineData("error-rate-limit.json", BankSyncFailure.Transient)]
    public async Task Plaids_failures_arrive_as_something_the_sync_engine_can_act_on(string payload, BankSyncFailure expected)
    {
        _plaid.AnswerWith(SyncPath, payload, HttpStatusCode.BadRequest);

        var e = await Assert.ThrowsAsync<BankSyncException>(() =>
            Connector().SyncAsync("access-token", cursor: null, Ct));

        Assert.Equal(expected, e.Kind);
    }

    [Fact]
    public async Task Exchanging_a_public_token_reads_the_institution_and_its_accounts()
    {
        _plaid.AnswerWith(ExchangePath, "item-public-token-exchange.json");
        _plaid.AnswerWith(AccountsPath, "accounts-get.json");

        var item = await Connector().ExchangeAsync("public-sandbox-token", Ct);

        Assert.Equal("access-sandbox-de3ce8ef", item.AccessToken);
        Assert.Equal("item-1", item.ProviderItemId);
        Assert.Equal("First Platypus Bank", item.InstitutionName);

        var account = Assert.Single(item.Accounts);

        Assert.Equal("acc-1", account.ProviderAccountId);
        Assert.Equal("Plaid Checking", account.Name);
        Assert.Equal("0000", account.Mask);
        Assert.Equal("depository", account.Type);
        Assert.Equal("checking", account.Subtype);
        Assert.Equal("USD", account.Currency);
    }

    [Fact]
    public async Task A_link_token_asks_for_transactions_and_says_where_to_send_webhooks()
    {
        _plaid.AnswerWith(LinkTokenPath, "link-token-create.json");

        var session = await Connector().CreateLinkSessionAsync(
            new LinkSessionRequest("user-1", "https://example.test/webhooks/plaid", null, null), Ct);

        Assert.Equal("link-sandbox-af1a0311", session.Token);

        var sent = JsonDocument.Parse(_plaid.Requests.Single().Body).RootElement;

        Assert.Equal("user-1", sent.GetProperty("user").GetProperty("client_user_id").GetString());
        Assert.Equal("https://example.test/webhooks/plaid", sent.GetProperty("webhook").GetString());
        Assert.Equal("transactions", sent.GetProperty("products")[0].GetString());
    }

    [Fact]
    public async Task Update_mode_names_the_item_and_asks_for_no_products()
    {
        _plaid.AnswerWith(LinkTokenPath, "link-token-create.json");

        await Connector().CreateLinkSessionAsync(
            new LinkSessionRequest("user-1", null, null, "access-token"), Ct);

        var sent = JsonDocument.Parse(_plaid.Requests.Single().Body).RootElement;

        Assert.Equal("access-token", sent.GetProperty("access_token").GetString());

        // Update mode is repairing a sign-in, not asking for anything new, and Plaid refuses
        // it if products are named.
        Assert.False(sent.TryGetProperty("products", out var products) && products.GetArrayLength() > 0);
    }

    [Theory]
    [InlineData("""{"webhook_type":"TRANSACTIONS","webhook_code":"SYNC_UPDATES_AVAILABLE","item_id":"item-1"}""", typeof(SyncUpdatesAvailable))]
    [InlineData("""{"webhook_type":"ITEM","webhook_code":"LOGIN_REPAIRED","item_id":"item-1"}""", typeof(LoginRepaired))]
    [InlineData("""{"webhook_type":"ITEM","webhook_code":"USER_PERMISSION_REVOKED","item_id":"item-1"}""", typeof(PermissionRevoked))]
    [InlineData("""{"webhook_type":"ITEM","webhook_code":"ERROR","item_id":"item-1","error":{"error_code":"ITEM_LOGIN_REQUIRED"}}""", typeof(LoginRequired))]
    [InlineData("""{"webhook_type":"ITEM","webhook_code":"PENDING_EXPIRATION","item_id":"item-1"}""", typeof(UnhandledWebhook))]
    [InlineData("""{"webhook_type":"ITEM","webhook_code":"ERROR","item_id":"item-1","error":{"error_code":"INSTITUTION_DOWN"}}""", typeof(UnhandledWebhook))]
    public void A_webhook_body_says_what_it_means(string body, Type expected)
    {
        var notification = Connector().ParseWebhook(body);

        Assert.IsType(expected, notification);
        Assert.Equal("item-1", notification.ProviderItemId);
    }
}

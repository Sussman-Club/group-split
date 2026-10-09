using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// What the CLI does with an expense that is not in dollars (#320): it can record one, it
/// says so wherever it prints the amount, its totals keep currencies apart, and the refusal
/// to move one into a group in another currency is exit code 3 with the API's code.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class CurrencyCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public CurrencyCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    // ---- Recording one ---------------------------------------------------------------

    [Fact]
    public async Task Transactions_create_sends_the_currency_it_was_given()
    {
        _api.Returns("/api/transactions", Transaction("Dinner in Lisbon", 20m, "EUR"));

        var result = await Cli.RunAsync(
            "transactions", "create", "Dinner in Lisbon", "20", "--currency", "EUR", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Method == "POST" && r.Path == "/api/transactions").Json;
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.Contains("20.00 EUR", result.Stdout);
    }

    [Fact]
    public async Task Transactions_create_without_a_currency_leaves_it_to_the_server()
    {
        _api.Returns("/api/transactions", Transaction("Coffee", 3m, "USD"));

        await Cli.RunAsync("transactions", "create", "Coffee", "3.00");

        // Absent rather than "USD": the server answers with the group's own currency when
        // there is a group, and a client that guessed would be refused in a euro group.
        var body = _api.Requests.Single(r => r.Method == "POST").Json;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("currency").ValueKind);
    }

    [Fact]
    public async Task Recording_into_a_group_in_another_currency_is_exit_3_with_the_apis_code()
    {
        _api.Problem("/api/transactions", 409, "CURRENCY_MISMATCH",
            "This is in EUR and the group keeps its balances in USD.",
            new { TransactionCurrency = "EUR", GroupCurrency = "USD" });

        var result = await Cli.RunAsync(
            "transactions", "create", "Dinner", "20", "--currency", "EUR", "--group", Guid.NewGuid().ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("CURRENCY_MISMATCH", result.Error.GetProperty("code").GetString());
        Assert.Contains("never converted", result.Error.GetProperty("remediation").GetString());
    }

    // ---- Moving one ------------------------------------------------------------------

    [Fact]
    public async Task Moving_an_expense_into_a_group_in_another_currency_is_exit_3_and_names_the_way_out()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", Transaction("Dinner in Lisbon", 20m, "EUR"), method: "GET");
        _api.Problem($"/api/transactions/{id}", 409, "CURRENCY_MISMATCH",
            "This is in EUR and the group keeps its balances in USD.",
            new { TransactionCurrency = "EUR", GroupCurrency = "USD" }, method: "PATCH");

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--group", Guid.NewGuid().ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("CURRENCY_MISMATCH", result.Error.GetProperty("code").GetString());

        var remediation = result.Error.GetProperty("remediation").GetString()!;
        Assert.Contains("personal", remediation);
        Assert.Contains("same currency", remediation);
    }

    // ---- The inbox -------------------------------------------------------------------

    /// <summary>
    /// <c>inbox file</c> without a group is how the CLI keeps a row personal, one at a time
    /// or in a loop -- the bulk "Keep personal" the web inbox offers. Nothing in the request
    /// names a currency, because the row already does; what comes back has to say it.
    /// </summary>
    [Fact]
    public async Task Inbox_file_without_a_group_keeps_a_foreign_row_personal_and_says_its_currency()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/inbox/{id}/file", Transaction("Pastelaria", 20m, "EUR"), status: 201);

        var result = await Cli.RunAsync("inbox", "file", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/inbox/{id}/file").Json;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("groupId").ValueKind);

        Assert.Contains("20.00 EUR", result.Stdout);
        Assert.Contains("your own ledger", result.Stdout);
    }

    [Fact]
    public async Task Filing_a_foreign_row_into_a_group_in_another_currency_is_exit_3()
    {
        var id = Guid.NewGuid();
        _api.Problem($"/api/inbox/{id}/file", 409, "CURRENCY_MISMATCH",
            "This is in EUR and the group keeps its balances in USD.",
            new { TransactionCurrency = "EUR", GroupCurrency = "USD" });

        var result = await Cli.RunAsync("inbox", "file", id.ToString(), "--group", Guid.NewGuid().ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("CURRENCY_MISMATCH", result.Error.GetProperty("code").GetString());
        Assert.Contains("without --group", result.Error.GetProperty("remediation").GetString());
    }

    // ---- Reading them back -----------------------------------------------------------

    [Fact]
    public async Task The_listing_names_a_currency_only_where_it_is_not_the_default()
    {
        _api.Returns("/api/transactions", Page(
            Transaction("Groceries", 10m, "USD"),
            Transaction("Dinner in Lisbon", 20m, "EUR")));

        var result = await Cli.RunAsync("transactions", "list", "--output", "text");

        Assert.Contains("20.00 EUR", result.Stdout);
        Assert.Contains("10.00", result.Stdout);
        Assert.DoesNotContain("10.00 USD", result.Stdout);
    }

    [Fact]
    public async Task The_summary_prints_other_currencies_beside_the_total_rather_than_in_it()
    {
        _api.Returns("/api/transactions/summary", new
        {
            count = 3,
            total = 25m,
            currency = "USD",
            otherCurrencies = new[] { new { currency = "EUR", count = 1, total = 20m } }
        });

        var result = await Cli.RunAsync("transactions", "summary", "--output", "text");

        Assert.Contains("25.00", result.Stdout);
        Assert.Contains("1 in EUR totalling 20.00 EUR", result.Stdout);
        Assert.DoesNotContain("45.00", result.Stdout);
    }

    [Fact]
    public async Task The_summary_as_json_carries_the_breakdown_as_the_api_sent_it()
    {
        _api.Returns("/api/transactions/summary", new
        {
            count = 3,
            total = 25m,
            currency = "USD",
            otherCurrencies = new[] { new { currency = "EUR", count = 1, total = 20m } }
        });

        var result = await Cli.RunAsync("transactions", "summary");

        Assert.Equal("USD", result.Json.GetProperty("currency").GetString());
        Assert.Equal("EUR", result.Json.GetProperty("otherCurrencies")[0].GetProperty("currency").GetString());
    }

    private static object Transaction(string name, decimal amount, string currency) => new
    {
        id = Guid.NewGuid(), name, description = (string?)null, amount, currency,
        dateTime = DateTimeOffset.UtcNow, groupId = (Guid?)null, groupName = (string?)null,
        paidByUserId = Guid.NewGuid(), paidByUserName = "Anabel",
        categoryId = (Guid?)null, category = (string?)null
    };

    private static object Page(params object[] items) => new
    {
        items, page = 1, pageSize = 25, totalCount = items.Length
    };
}

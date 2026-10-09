using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// An expense's currency over the wire (#320): the field binds and is validated, the
/// refusals arrive as <c>CURRENCY_MISMATCH</c>, and the responses say what everything is in.
/// </summary>
/// <remarks>
/// The rules themselves are <c>TransactionCurrencyTest</c>'s and <c>TransactionMoveTest</c>'s.
/// These are about the parts a direct service call skips -- the validation filter, the
/// problem document a client reads the code from, and the monthly series, which is written
/// inside its endpoint.
/// </remarks>
public class CurrencyEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task<HttpResponseMessage> Create(decimal amount, string? currency, Guid? groupId = null,
        DateTimeOffset? when = null) =>
        Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            Currency = currency,
            GroupId = groupId,
            DateTime = when ?? DateTimeOffset.UtcNow
        }, Json, Ct);

    private async Task<Guid> AGroup()
    {
        var response = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = "Flat" }, Json, Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("code").GetString();

    [Fact]
    public async Task A_personal_expense_created_in_euros_comes_back_in_euros()
    {
        var response = await Create(20m, "eur");

        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;

        var read = await Client.GetFromJsonAsync<TransactionDetailsResponse>($"/transactions/{created.Id}", Json, Ct);

        Assert.Equal("EUR", created.Currency);
        Assert.Equal("EUR", read!.Currency);
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("E1")]
    [InlineData("€")]
    public async Task A_currency_that_is_not_a_three_letter_code_is_a_validation_failure(string currency)
    {
        var response = await Create(20m, currency);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recording_into_a_group_in_another_currency_is_a_409_currency_mismatch()
    {
        var group = await AGroup();

        var response = await Create(20m, "EUR", group);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CURRENCY_MISMATCH", await CodeOf(response));
    }

    [Fact]
    public async Task Moving_a_euro_expense_into_a_dollar_group_is_a_409_and_changes_nothing()
    {
        var group = await AGroup();
        var created = (await (await Create(20m, "EUR")).Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;

        var response = await Client.PatchAsync($"/transactions/{created.Id}",
            new StringContent(
                JsonSerializer.Serialize(new[] { new { op = "replace", path = "/groupId", value = group } }),
                Encoding.UTF8, "application/json-patch+json"),
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CURRENCY_MISMATCH", await CodeOf(response));

        var read = await Client.GetFromJsonAsync<TransactionDetailsResponse>($"/transactions/{created.Id}", Json, Ct);
        Assert.Null(read!.GroupId);
        Assert.Equal("EUR", read.Currency);
    }

    [Fact]
    public async Task The_summary_reports_another_currency_beside_the_total_rather_than_in_it()
    {
        (await Create(10m, null)).EnsureSuccessStatusCode();
        (await Create(20m, "EUR")).EnsureSuccessStatusCode();

        var summary = await Client.GetFromJsonAsync<TransactionSummaryResponse>("/transactions/summary", Json, Ct);

        Assert.Equal(2, summary!.Count);
        Assert.Equal("USD", summary.Currency);
        Assert.Equal(10m, summary.Total);
        Assert.Equal(new CurrencyTotalResponse("EUR", 1, 20m), Assert.Single(summary.OtherCurrencies));
    }

    [Fact]
    public async Task The_monthly_series_is_drawn_in_one_currency_and_leaves_the_others_out()
    {
        var march = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

        (await Create(10m, null, when: march)).EnsureSuccessStatusCode();
        (await Create(20m, "EUR", when: march)).EnsureSuccessStatusCode();

        var months = await Client.GetFromJsonAsync<MonthlyExposureResponse[]>("/transactions/monthly", Json, Ct);

        var month = Assert.Single(months!);
        Assert.Equal("USD", month.Currency);
        Assert.Equal(10m, month.Paid);
        Assert.Equal(10m, month.Share);
    }
}

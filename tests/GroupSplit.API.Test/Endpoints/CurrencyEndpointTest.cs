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
    [InlineData("ZZZ")]
    [InlineData("CLF")]
    public async Task A_currency_that_is_not_a_three_letter_code_is_a_validation_failure(string currency)
    {
        var response = await Create(20m, currency);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recording_into_a_group_in_another_currency_preserves_it()
    {
        var group = await AGroup();

        var response = await Create(20m, "EUR", group);

        response.EnsureSuccessStatusCode();
        Assert.Equal("EUR", (await response.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!.Currency);
    }

    [Fact]
    public async Task Moving_a_euro_expense_into_a_dollar_group_preserves_its_currency()
    {
        var group = await AGroup();
        var created = (await (await Create(20m, "EUR")).Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;

        var response = await Client.PatchAsync($"/transactions/{created.Id}",
            new StringContent(
                JsonSerializer.Serialize(new[] { new { op = "replace", path = "/groupId", value = group } }),
                Encoding.UTF8, "application/json-patch+json"),
            Ct);

        response.EnsureSuccessStatusCode();

        var read = await Client.GetFromJsonAsync<TransactionDetailsResponse>($"/transactions/{created.Id}", Json, Ct);
        Assert.Equal(group, read!.GroupId);
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
    [Fact]
    public async Task Household_month_uses_local_boundaries_and_keeps_personal_currencies_separate()
    {
        var me = await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct);
        var group = me!.Id;
        // New York changes from UTC-5 to UTC-4 during March.
        (await Create(100m, "USD", when: DateTimeOffset.Parse("2026-03-01T04:59:59Z"))).EnsureSuccessStatusCode();
        (await Create(10m, "USD", when: DateTimeOffset.Parse("2026-03-01T05:00:00Z"))).EnsureSuccessStatusCode();
        (await Create(20m, "EUR", when: DateTimeOffset.Parse("2026-04-01T03:59:59Z"))).EnsureSuccessStatusCode();
        (await Create(200m, "EUR", when: DateTimeOffset.Parse("2026-04-01T04:00:00Z"))).EnsureSuccessStatusCode();

        var result = await Client.GetFromJsonAsync<HouseholdMonthResponse>(
            $"/groups/{group}/household-month?year=2026&month=3&timeZone=America%2FNew_York", Json, Ct);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T05:00:00Z"), result!.From);
        Assert.Equal(DateTimeOffset.Parse("2026-04-01T04:00:00Z"), result.Until);
        Assert.Equal(new[] { new HouseholdCurrencyExposure("EUR", 20m, 20m), new HouseholdCurrencyExposure("USD", 10m, 10m) }, result.Exposure);
        Assert.Equal(10m, Assert.Single(result.Spending.Categories).Total);
        Assert.Equal(20m, Assert.Single(Assert.Single(result.Spending.OtherCurrencies).Categories).Total);
    }

    [Fact]
    public async Task Household_month_rejects_invalid_inputs_and_other_users_groups()
    {
        var group = await AGroup();
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/groups/{group}/household-month?year=2026&month=13&timeZone=UTC", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/groups/{group}/household-month?year=2026&month=3&timeZone=Not%2FAZone", Ct)).StatusCode);
        using var other = _host.ClientForAnotherUser();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/groups/{group}/household-month?year=2026&month=3&timeZone=UTC", Ct)).StatusCode);
    }
    [Fact]
    public async Task Household_month_distinguishes_amount_paid_from_callers_share()
    {
        var group = await AGroup();
        var me = await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct);
        var response = await Client.PostAsJsonAsync($"/groups/{group}/guests", new AddGuestsRequest { Names = ["Pat"] }, Json, Ct);
        response.EnsureSuccessStatusCode();
        var guest = Assert.Single((await response.Content.ReadFromJsonAsync<GroupGuestResponse[]>(Json, Ct))!);
        var expense = new CreateTransactionRequest
        {
            Name = "Dinner",
            GroupId = group,
            Amount = 40m,
            DateTime = DateTimeOffset.Parse("2026-03-15T12:00:00Z"),
            PaidByUserId = guest.UserId,
            Splits = [new SplitInput { UserId = me!.Id, Amount = 15m }, new SplitInput { UserId = guest.UserId, Amount = 25m }]
        };
        (await Client.PostAsJsonAsync("/transactions", expense, Json, Ct)).EnsureSuccessStatusCode();
        var result = await Client.GetFromJsonAsync<HouseholdMonthResponse>($"/groups/{group}/household-month?year=2026&month=3&timeZone=UTC", Json, Ct);
        Assert.Equal(new HouseholdCurrencyExposure("USD", 0m, 15m), Assert.Single(result!.Exposure));
        Assert.Equal(40m, Assert.Single(result.Spending.Categories).Total);
    }
}

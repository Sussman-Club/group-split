using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Test;

/// <summary>
/// <c>--through &lt;day&gt;</c> on every command that reads or settles what is owed (#247),
/// and <c>--covers-through</c> on a settlement's edit.
/// </summary>
/// <remarks>
/// What the day means is the API's to decide. What these check is that it arrives -- as the
/// day, with this machine's offset beside it -- on the read the command makes first as well as
/// on the write, since a confirmation built from the whole balance and a write made through a
/// day would be asking somebody to agree to one amount and recording another.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class SettleThroughCommandTests : IDisposable
{
    private static readonly Guid Daniel = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly DateOnly EndOfAugust = new(2026, 8, 31);

    private static readonly string ExpectedOffset =
        ((int)TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 1)).TotalMinutes).ToString();

    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public SettleThroughCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    [Fact]
    public async Task Settle_plan_asks_for_the_plan_through_the_day()
    {
        _api.Returns("/api/users/me/settlement-plan", Plan());

        var result = await Cli.RunAsync("settle", "plan", "--through", "2026-08-31", "--json");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var read = _api.Requests.Single(request => request.Path == "/api/users/me/settlement-plan");
        Assert.Equal("2026-08-31", read.Parameter("through"));
        Assert.Equal(ExpectedOffset, read.Parameter("utcOffset"));
    }

    [Fact]
    public async Task Settle_plan_without_a_day_sends_none()
    {
        _api.Returns("/api/users/me/settlement-plan", Plan());

        await Cli.RunAsync("settle", "plan", "--json");

        var read = _api.Requests.Single(request => request.Path == "/api/users/me/settlement-plan");
        Assert.Null(read.Parameter("through"));
        Assert.Null(read.Parameter("utcOffset"));
    }

    [Fact]
    public async Task Settle_pay_reads_and_writes_through_the_same_day()
    {
        _api.Returns("/api/users/me/settlement-plan", Plan());
        _api.Returns("/api/users/me/settle", Recorded());

        var result = await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(), "--direction", "youpaidthem",
            "--through", "2026-08-31", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var read = _api.Requests.Single(request => request.Path == "/api/users/me/settlement-plan");
        Assert.Equal("2026-08-31", read.Parameter("through"));

        var body = _api.Requests.Single(request => request.Path == "/api/users/me/settle").Json;
        Assert.Equal("2026-08-31", body.GetProperty("through").GetString());
        Assert.Equal(int.Parse(ExpectedOffset), body.GetProperty("utcOffset").GetInt32());
    }

    /// <summary>
    /// The command a confirmation hands back has to settle the same period it was asked
    /// about, or confirming it would record something else.
    /// </summary>
    [Fact]
    public async Task Settle_pay_s_confirmation_carries_the_day()
    {
        _api.Returns("/api/users/me/settlement-plan", Plan());

        var result = await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(), "--direction", "youpaidthem", "--through", "2026-08-31");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("--through 2026-08-31", result.Json.GetProperty("confirmCommand").GetString());
    }

    [Fact]
    public async Task Groups_balances_through_a_day_asks_for_them_through_it()
    {
        var group = Guid.NewGuid();
        _api.Returns($"/api/groups/{group}/balances", Balance());

        await Cli.RunAsync("groups", "balances", group.ToString(), "--through", "2026-08-31", "--json");

        var read = _api.Requests.Single(request => request.Path == $"/api/groups/{group}/balances");
        Assert.Equal("2026-08-31", read.Parameter("through"));
        Assert.Equal(ExpectedOffset, read.Parameter("utcOffset"));
    }

    [Fact]
    public async Task Groups_settle_up_through_a_day_reads_and_writes_through_it()
    {
        var group = Guid.NewGuid();
        _api.Returns($"/api/groups/{group}/balances", Balance());
        _api.Returns($"/api/groups/{group}/settle-up", Settled());

        var result = await Cli.RunAsync("groups", "settle-up", group.ToString(), "--through", "2026-08-31", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var read = _api.Requests.Single(request => request.Path == $"/api/groups/{group}/balances");
        Assert.Equal("2026-08-31", read.Parameter("through"));

        var body = _api.Requests.Single(request => request.Path == $"/api/groups/{group}/settle-up").Json;
        Assert.Equal("2026-08-31", body.GetProperty("through").GetString());
        Assert.Equal(int.Parse(ExpectedOffset), body.GetProperty("utcOffset").GetInt32());
    }

    [Fact]
    public async Task Groups_settle_up_s_confirmation_carries_the_day()
    {
        var group = Guid.NewGuid();
        _api.Returns($"/api/groups/{group}/balances", Balance());

        var result = await Cli.RunAsync("groups", "settle-up", group.ToString(), "--through", "2026-08-31");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("--through 2026-08-31", result.Json.GetProperty("confirmCommand").GetString());
    }

    [Fact]
    public async Task Groups_settle_records_the_day_it_was_given()
    {
        var group = Guid.NewGuid();
        var omar = Guid.NewGuid();
        _api.Returns($"/api/groups/{group}/members", new[] { new { id = omar, firstName = "Omar" } });
        _api.NoContent($"/api/groups/{group}/settle");

        await Cli.RunAsync("groups", "settle", group.ToString(), omar.ToString(), "40.00",
            "--through", "2026-08-31", "--yes");

        var body = _api.Requests.Single(request => request.Path == $"/api/groups/{group}/settle").Json;
        Assert.Equal("2026-08-31", body.GetProperty("through").GetString());
    }

    // ---- A settlement's period, edited -------------------------------------------------

    [Fact]
    public async Task A_settlement_can_be_given_a_day_it_covers_through()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", ASettlement(id, coversThrough: null));

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--covers-through", "2026-08-31");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var operation = Assert.Single(_api.Requests, request => request.Method == "PATCH").Json.EnumerateArray().Single();
        Assert.Equal("/coversThrough", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal("2026-08-31", operation.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_settlements_day_can_be_cleared()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", ASettlement(id, EndOfAugust));

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--no-covers-through");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var operation = Assert.Single(_api.Requests, request => request.Method == "PATCH").Json.EnumerateArray().Single();
        Assert.Equal("/coversThrough", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(JsonValueKind.Null, operation.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task An_edit_that_says_nothing_about_the_period_keeps_it()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", ASettlement(id, EndOfAugust));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--description", "cash");

        var operations = Assert.Single(_api.Requests, request => request.Method == "PATCH").Json.EnumerateArray();
        Assert.DoesNotContain(operations, operation =>
            string.Equals(operation.GetProperty("path").GetString(), "/coversThrough", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_expense_cannot_be_given_a_period()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", new { id, kind = 0, name = "Dinner", amount = 40m });

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--covers-through", "2026-08-31");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.DoesNotContain(_api.Requests, request => request.Method == "PATCH");
    }

    [Fact]
    public async Task Showing_a_settlement_says_what_it_covers_and_what_is_still_owed()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", ASettlement(id, EndOfAugust, stillOwed: 12.50m));

        var result = await Cli.RunAsync("transactions", "show", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("2026-08-31", result.Stdout);
        Assert.Contains("12.50", result.Stdout);
        Assert.NotNull(_api.Requests.Single(request => request.Path == $"/api/transactions/{id}").Parameter("utcOffset"));
    }

    // ---- Fixtures --------------------------------------------------------------------

    private static object ASettlement(Guid id, DateOnly? coversThrough, decimal stillOwed = 0m) => new
    {
        id,
        kind = 1,
        name = "Settlement",
        amount = 40m,
        paidByUserName = "Daniel",
        paidToUserName = "Me",
        coverage = coversThrough is { } day
            ? new { through = day.ToString("yyyy-MM-dd"), from = (string?)null, stillOwed }
            : null
    };

    private static object Plan() => new
    {
        net = -58.40m,
        owedToYouTotal = 0m,
        youOweTotal = 58.40m,
        youPay = new[]
        {
            new
            {
                userId = Daniel,
                userName = "Daniel",
                amount = 58.40m,
                groups = new[] { new { groupId = Home, groupName = "Home", amount = 58.40m } },
                isGuest = false
            }
        },
        owedToYou = Array.Empty<object>(),
        lastSettled = (DateTimeOffset?)null
    };

    private static object Recorded() => new
    {
        userId = Daniel,
        userName = "Daniel",
        amount = 58.40m,
        direction = (int)SettlementDirection.YouPaidThem,
        date = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
        groups = new[] { new { groupId = Home, groupName = "Home", amount = 58.40m } },
        coversThrough = "2026-08-31"
    };

    private static object Balance() => new
    {
        netBalances = Array.Empty<object>(),
        owedToYou = new[] { new { userId = Guid.NewGuid(), userName = "Omar", amount = 25.00m } },
        youOwed = Array.Empty<object>()
    };

    private static object Settled() => new
    {
        payments = Array.Empty<object>(),
        date = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
        coversThrough = "2026-08-31"
    };
}

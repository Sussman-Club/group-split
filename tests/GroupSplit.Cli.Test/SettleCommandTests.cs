using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Test;

/// <summary>
/// Settling with a person rather than inside a group.
/// </summary>
/// <remarks>
/// The command reads the plan before it writes, for the same reason <c>groups settle</c>
/// reads the roster: so the confirmation names what is about to change. Here there is a
/// second reason -- one payment can land in two groups, and a confirmation that did not say
/// which would be asking somebody to agree to bookkeeping they cannot see.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class SettleCommandTests : IDisposable
{
    private static readonly Guid Daniel = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Vacation = Guid.NewGuid();

    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public SettleCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");

        _api.Returns("/api/users/me/settlement-plan", Plan());
        _api.Returns("/api/users/me/settle", Recorded());
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    /// <summary>
    /// The plan reads as one line per person, with the groups it comes from named so the
    /// arithmetic is checkable on the row.
    /// </summary>
    [Fact]
    public async Task Plan_lists_one_line_per_person_and_names_the_groups_behind_it()
    {
        var result = await Cli.RunAsync("settle", "plan", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Daniel", result.Stdout);
        Assert.Contains("Home", result.Stdout);
        Assert.Contains("Vacation", result.Stdout);
    }

    [Fact]
    public async Task Plan_as_json_carries_both_sides_and_the_totals()
    {
        var result = await Cli.RunAsync("settle", "plan", "--json");

        Assert.Equal(58.40m, result.Json.GetProperty("youOweTotal").GetDecimal());
        Assert.Equal(Daniel, result.Json.GetProperty("youPay")[0].GetProperty("userId").GetGuid());
        Assert.Equal(2, result.Json.GetProperty("youPay")[0].GetProperty("groups").GetArrayLength());
    }

    /// <summary>
    /// The whole promise of the command in one test: one payment, and the groups it will be
    /// spread over shown before anything is written.
    /// </summary>
    [Fact]
    public async Task Pay_dry_run_shows_the_per_group_breakdown_and_writes_nothing()
    {
        var result = await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(),
            "--direction", "youpaidthem", "--dry-run", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Home", result.Stdout);
        Assert.Contains("Vacation", result.Stdout);

        Assert.DoesNotContain(_api.Requests, request => request.Path == "/api/users/me/settle");
    }

    /// <summary>
    /// Largest group first, as the API spreads it. The command says what the server will do
    /// rather than deciding it, and the two have to agree or the confirmation is a lie.
    /// </summary>
    [Fact]
    public async Task Pay_part_of_it_says_it_clears_the_largest_group_first()
    {
        var result = await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(), "--amount", "40",
            "--direction", "youpaidthem", "--dry-run", "--json");

        var groups = result.Json.GetProperty("groups");

        Assert.Equal(1, groups.GetArrayLength());
        Assert.Contains("Vacation", groups[0].GetString());
    }

    [Fact]
    public async Task Pay_sends_the_person_the_amount_and_the_direction()
    {
        var result = await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(), "--amount", "58.40",
            "--direction", "youpaidthem", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(request => request.Path == "/api/users/me/settle").Json;

        Assert.Equal(Daniel, body.GetProperty("userId").GetGuid());
        Assert.Equal(58.40m, body.GetProperty("amount").GetDecimal());
        Assert.Equal(
            SettlementDirection.YouPaidThem,
            (SettlementDirection)body.GetProperty("direction").GetInt32());
    }

    /// <summary>
    /// The amount defaults to the whole of what is outstanding between the two of them,
    /// which the command knows because it read the plan.
    /// </summary>
    [Fact]
    public async Task Pay_without_an_amount_settles_the_whole_of_it()
    {
        await Cli.RunAsync("settle", "pay", Daniel.ToString(), "--direction", "youpaidthem", "--yes");

        var body = _api.Requests.Single(request => request.Path == "/api/users/me/settle").Json;

        Assert.Equal(58.40m, body.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Pay_sends_the_date_and_the_note()
    {
        await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(), "--direction", "youpaidthem",
            "--date", "2026-09-30", "--note", "bank transfer", "--yes");

        var body = _api.Requests.Single(request => request.Path == "/api/users/me/settle").Json;

        Assert.Equal(new DateTime(2026, 9, 30), body.GetProperty("date").GetDateTimeOffset().Date);
        Assert.Equal("bank transfer", body.GetProperty("description").GetString());
    }

    /// <summary>
    /// The gate every destructive command passes through, and this one is no exception:
    /// without a terminal to prompt, it stops at exit 4 with the command that would do it.
    /// </summary>
    [Fact]
    public async Task Pay_without_yes_asks_for_confirmation_and_writes_nothing()
    {
        var result = await Cli.RunAsync("settle", "pay", Daniel.ToString(), "--direction", "youpaidthem");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.DoesNotContain(_api.Requests, request => request.Path == "/api/users/me/settle");

        // The changes it lists are the groups the payment lands in, which is the thing
        // somebody is actually being asked to agree to.
        var changes = result.Json.GetProperty("changes");

        Assert.Equal(2, changes.GetArrayLength());
    }

    /// <summary>
    /// Being square with somebody is a perfectly good answer to "settle with them". A
    /// script that had to read a 409 to find that out would treat squareness as a failure.
    /// </summary>
    [Fact]
    public async Task Pay_when_there_is_nothing_outstanding_succeeds_and_says_so()
    {
        var stranger = Guid.NewGuid();

        var result = await Cli.RunAsync("settle", "pay", stranger.ToString(), "--yes", "--json");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("nothing-outstanding", result.Json.GetProperty("status").GetString());
        Assert.DoesNotContain(_api.Requests, request => request.Path == "/api/users/me/settle");
    }

    /// <summary>
    /// The direction decides which half of the plan is consulted. Asking to record a payment
    /// you made to somebody who owes you finds nothing, rather than quietly writing a debt
    /// the wrong way round.
    /// </summary>
    [Fact]
    public async Task Pay_in_the_wrong_direction_finds_nothing_to_settle()
    {
        var result = await Cli.RunAsync(
            "settle", "pay", Daniel.ToString(), "--direction", "theypaidyou", "--yes", "--json");

        Assert.Equal("nothing-outstanding", result.Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task History_lists_the_repayments_and_the_group_each_landed_in()
    {
        _api.Returns("/api/users/me/settlements", new
        {
            items = new object[]
            {
                new
                {
                    id = Guid.NewGuid(), groupId = Home, groupName = "Home",
                    fromUserId = Guid.NewGuid(), fromUserName = "Omar Rivero",
                    toUserId = Guid.NewGuid(), toUserName = "Anabel Benitez",
                    amount = 40m, dateTime = DateTimeOffset.UtcNow,
                    description = "cash", paidByYou = false
                }
            },
            page = 1, pageSize = 25, totalCount = 1
        });

        var result = await Cli.RunAsync("settle", "history", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Omar Rivero paid you", result.Stdout);
        Assert.Contains("Home", result.Stdout);
    }

    // ---- the plan the stub answers with ----------------------------------------------

    /// <summary>
    /// Daniel in two groups: 18.40 in Home and 40.00 in Vacation, which is one payment of
    /// 58.40 and the case the whole feature exists for.
    /// </summary>
    private static object Plan() => new
    {
        net = -58.40m,
        owedToYouTotal = 0m,
        youOweTotal = 58.40m,
        youPay = new object[]
        {
            new
            {
                userId = Daniel,
                userName = "Daniel Rivero",
                amount = 58.40m,
                groups = new object[]
                {
                    new { groupId = Vacation, groupName = "Vacation 2024", amount = 40.00m },
                    new { groupId = Home, groupName = "Home", amount = 18.40m }
                }
            }
        },
        owedToYou = Array.Empty<object>(),
        lastSettled = (DateTimeOffset?)null
    };

    private static object Recorded() => new
    {
        userId = Daniel,
        userName = "Daniel Rivero",
        amount = 58.40m,
        direction = (int)SettlementDirection.YouPaidThem,
        date = DateTimeOffset.UtcNow,
        description = (string?)null,
        groups = new object[]
        {
            new { groupId = Vacation, groupName = "Vacation 2024", amount = 40.00m },
            new { groupId = Home, groupName = "Home", amount = 18.40m }
        }
    };
}

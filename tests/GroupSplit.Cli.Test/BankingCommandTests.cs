using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The bank and inbox commands, driven the way <see cref="CommandTests"/> drives the rest:
/// real tree, real generated client, real HTTP against a stub. What they assert is what
/// left on the wire, because a filter or a share that never arrived is indistinguishable
/// from one the server ignored -- the command succeeds either way and the answer is wrong.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class BankingCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public BankingCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    // ---- bank ------------------------------------------------------------------------

    [Fact]
    public async Task Bank_list_shows_the_connections_and_their_accounts()
    {
        _api.Returns("/api/bank-connections", Connections(Connection("Monzo")));

        var result = await Cli.RunAsync("bank", "list", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Monzo", result.Stdout);
        Assert.Contains("Current account", result.Stdout);
        Assert.Contains("4321", result.Stdout);
    }

    [Fact]
    public async Task Bank_list_says_bank_sync_is_off_rather_than_showing_an_empty_list()
    {
        _api.Returns("/api/bank-connections", new { enabled = false, connections = Array.Empty<object>() });

        var result = await Cli.RunAsync("bank", "list", "--output", "text");

        // "No banks linked" and "this deployment has no provider" are different answers,
        // and only one of them is worth trying to fix by linking one.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("not configured", result.Stdout);
    }

    [Fact]
    public async Task Bank_list_reports_a_connection_that_needs_signing_in_again()
    {
        _api.Returns("/api/bank-connections", Connections(Connection("Monzo", status: "LoginRequired")));

        var result = await Cli.RunAsync("bank", "list");

        Assert.Equal(
            "LoginRequired",
            result.Json.GetProperty("connections")[0].GetProperty("status").GetString());
        Assert.True(result.Json.GetProperty("connections")[0].GetProperty("needsAttention").GetBoolean());
    }

    [Fact]
    public async Task Bank_link_token_asks_for_an_update_token_when_given_a_connection()
    {
        var id = Guid.NewGuid();
        _api.Returns("/api/bank-connections/link-token", new
        {
            token = "link-sandbox-1", expiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        });

        var result = await Cli.RunAsync("bank", "link-token", "--connection", id.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("link-sandbox-1", result.Json.GetProperty("token").GetString());

        // Without the id the provider opens a fresh link instead of repairing this one,
        // which quietly leaves the person with two copies of the same bank.
        var body = _api.Requests.Single(r => r.Path == "/api/bank-connections/link-token").Json;
        Assert.Equal(id, body.GetProperty("connectionId").GetGuid());
    }

    [Fact]
    public async Task Bank_link_exchanges_the_public_token_it_was_given()
    {
        _api.Returns("/api/bank-connections", Connection("Monzo"), status: 201);

        var result = await Cli.RunAsync("bank", "link", "public-sandbox-9");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Method == "POST").Json;
        Assert.Equal("public-sandbox-9", body.GetProperty("publicToken").GetString());
    }

    [Fact]
    public async Task Bank_sync_reports_the_queue_rather_than_claiming_it_is_done()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/bank-connections/{id}/sync", new { }, status: 202);

        var result = await Cli.RunAsync("bank", "sync", id.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("queued", result.Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Bank_unlink_without_a_terminal_exits_4_and_names_the_bank()
    {
        var id = Guid.NewGuid();
        _api.Returns("/api/bank-connections", Connections(Connection("Monzo", id)));

        var result = await Cli.RunAsync("bank", "unlink", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("bank.unlink", result.Json.GetProperty("action").GetString());
        Assert.Contains("Monzo", result.Json.GetProperty("summary").GetString());
        Assert.Equal(
            $"groupsplit bank unlink {id} --yes",
            result.Json.GetProperty("confirmCommand").GetString());

        Assert.DoesNotContain(_api.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task Bank_unlink_with_yes_goes_through()
    {
        var id = Guid.NewGuid();
        _api.Returns("/api/bank-connections", Connections(Connection("Monzo", id)));
        _api.NoContent($"/api/bank-connections/{id}");

        var result = await Cli.RunAsync("bank", "unlink", id.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_api.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task Bank_unlink_of_an_id_that_is_not_linked_fails_before_deleting_anything()
    {
        _api.Returns("/api/bank-connections", Connections(Connection("Monzo")));

        var result = await Cli.RunAsync("bank", "unlink", Guid.NewGuid().ToString(), "--yes");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("bank list", result.Error.GetProperty("remediation").GetString());
        Assert.DoesNotContain(_api.Requests, r => r.Method == "DELETE");
    }

    // ---- inbox -----------------------------------------------------------------------

    [Fact]
    public async Task Inbox_list_shows_the_rows_with_the_account_they_came_from()
    {
        _api.Returns("/api/inbox", Page(Row("Sainsbury's", 42.10m)));

        var result = await Cli.RunAsync("inbox", "list", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Sainsbury's", result.Stdout);
        Assert.Contains("42.10", result.Stdout);
        Assert.Contains("Monzo", result.Stdout);
        // The footer, without which a first page of many looks like the whole inbox.
        Assert.Contains("1 total", result.Stdout);
    }

    [Fact]
    public async Task Inbox_list_leads_a_row_with_the_date_it_was_spent_not_the_date_it_posted()
    {
        _api.Returns("/api/inbox", Page(Row("Sainsbury's", 42.10m,
            date: new DateOnly(2026, 3, 9), authorized: new DateOnly(2026, 3, 4))));

        var result = await Cli.RunAsync("inbox", "list", "--output", "text");

        // A card charge often posts days after the event, and the posting date is not the
        // one anybody remembers spending it on.
        Assert.Contains("2026-03-04", result.Stdout);
        Assert.DoesNotContain("2026-03-09", result.Stdout);
    }

    [Fact]
    public async Task Inbox_list_sends_every_filter_it_was_given()
    {
        _api.Returns("/api/inbox", Page());

        await Cli.RunAsync(
            "inbox", "list",
            "--status", "ignored",
            "--sort-by", "amount",
            "--order", "asc",
            "--page", "3",
            "--page-size", "10");

        var request = _api.Requests.Single(r => r.Path == "/api/inbox");

        // Read back through the enum rather than compared as text: the generated client
        // may send the name or the number, and both are the same filter to the API.
        Assert.Equal(
            InboxStatus.Ignored,
            Enum.Parse<InboxStatus>(request.Parameter("Status")!, ignoreCase: true));
        Assert.Equal("amount", request.Parameter("SortBy"));
        Assert.Equal("false", request.Parameter("SortDescending"));
        Assert.Equal("3", request.Parameter("Page"));
        Assert.Equal("10", request.Parameter("PageSize"));
    }

    [Fact]
    public async Task Inbox_list_sends_no_sort_direction_when_none_was_asked_for()
    {
        _api.Returns("/api/inbox", Page());

        await Cli.RunAsync("inbox", "list");

        // Absent, not false: the server's own default runs the other way for a date, and
        // sending false would silently reverse every listing that did not ask.
        Assert.Null(_api.Requests.Single(r => r.Path == "/api/inbox").Parameter("SortDescending"));
    }

    [Fact]
    public async Task Inbox_summary_counts_what_is_waiting()
    {
        _api.Returns("/api/inbox/summary", new { newCount = 7 });

        var result = await Cli.RunAsync("inbox", "summary", "--output", "text");

        Assert.Contains("7", result.Stdout);
    }

    [Fact]
    public async Task Inbox_file_sends_the_group_category_and_payer_it_was_given()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var payer = Guid.NewGuid();

        _api.Returns($"/api/inbox/{id}/file", Transaction("Sainsbury's", 42.10m, groupId), status: 201);

        var result = await Cli.RunAsync(
            "inbox", "file", id.ToString(),
            "--group", groupId.ToString(),
            "--category-id", categoryId.ToString(),
            "--paid-by", payer.ToString(),
            "--name", "Weekly shop",
            "--description", "Flat groceries");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/inbox/{id}/file").Json;

        Assert.Equal(groupId, body.GetProperty("groupId").GetGuid());
        Assert.Equal(categoryId, body.GetProperty("categoryId").GetGuid());
        Assert.Equal(payer, body.GetProperty("paidByUserId").GetGuid());
        Assert.Equal("Weekly shop", body.GetProperty("name").GetString());
        Assert.Equal("Flat groceries", body.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Inbox_file_sends_no_splits_at_all_when_none_were_named()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/inbox/{id}/file", Transaction("Sainsbury's", 42.10m, null), status: 201);

        await Cli.RunAsync("inbox", "file", id.ToString());

        // Null, not an empty list: an empty one is "divide it between nobody", which the
        // API refuses, while absent means "divide it the way the category says".
        var body = _api.Requests.Single(r => r.Path == $"/api/inbox/{id}/file").Json;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("splits").ValueKind);
    }

    [Fact]
    public async Task Inbox_file_sends_the_exact_shares_when_they_were_named()
    {
        var id = Guid.NewGuid();
        var anabel = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/inbox/{id}/file", Transaction("Sainsbury's", 42.10m, Guid.NewGuid()), status: 201);

        await Cli.RunAsync(
            "inbox", "file", id.ToString(),
            "--split", $"{anabel}=30.10",
            "--split", $"{omar}=12.00");

        var splits = _api.Requests.Single(r => r.Path == $"/api/inbox/{id}/file").Json
            .GetProperty("splits");

        Assert.Equal(2, splits.GetArrayLength());
        Assert.Equal(anabel, splits[0].GetProperty("userId").GetGuid());
        Assert.Equal(30.10m, splits[0].GetProperty("amount").GetDecimal());
        Assert.Equal(12.00m, splits[1].GetProperty("amount").GetDecimal());
    }

    [Theory]
    [InlineData("not-a-pair")]
    [InlineData("not-a-guid=12.00")]
    [InlineData("3f25c1a8-1111-2222-3333-444455556666=lots")]
    public async Task A_malformed_share_is_refused_before_anything_is_sent(string split)
    {
        var result = await Cli.RunAsync("inbox", "file", Guid.NewGuid().ToString(), "--split", split);

        // Exit code 3, and nothing on the wire: a share the server never received is not an
        // error it can report -- the totals just come out somebody short.
        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal(ErrorCodes.InvalidInput, result.Error.GetProperty("code").GetString());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Two_shares_for_one_person_are_refused_rather_than_one_winning()
    {
        var anabel = Guid.NewGuid();

        var result = await Cli.RunAsync(
            "inbox", "file", Guid.NewGuid().ToString(),
            "--split", $"{anabel}=10.00",
            "--split", $"{anabel}=20.00");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Inbox_ignore_and_restore_are_each_one_call_and_say_how_to_undo()
    {
        var id = Guid.NewGuid();
        _api.NoContent($"/api/inbox/{id}/ignore");
        _api.NoContent($"/api/inbox/{id}/restore");

        var ignored = await Cli.RunAsync("inbox", "ignore", id.ToString());
        var restored = await Cli.RunAsync("inbox", "restore", id.ToString());

        Assert.Equal("ignored", ignored.Json.GetProperty("status").GetString());
        Assert.Equal("restored", restored.Json.GetProperty("status").GetString());
        Assert.Equal(2, _api.Requests.Count);
    }

    [Fact]
    public async Task A_row_the_bank_withdrew_is_reported_with_the_api_s_own_code()
    {
        var id = Guid.NewGuid();
        _api.Problem($"/api/inbox/{id}/file", 409, "BANK_TRANSACTION_ALREADY_FILED",
            "This row has already been filed.");

        var result = await Cli.RunAsync("inbox", "file", id.ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("BANK_TRANSACTION_ALREADY_FILED", result.Error.GetProperty("code").GetString());
    }

    private static object Connections(params object[] connections) => new
    {
        enabled = true, connections
    };

    private static object Connection(string institution, Guid? id = null, string status = "Active") => new
    {
        id = id ?? Guid.NewGuid(),
        provider = "plaid",
        institutionName = institution,
        status,
        linkedAt = DateTimeOffset.UtcNow.AddDays(-30),
        lastSyncedAt = DateTimeOffset.UtcNow.AddHours(-2),
        accounts = new[]
        {
            new
            {
                id = Guid.NewGuid(), name = "Current account", mask = "4321",
                type = "depository", subtype = "checking", currency = "GBP"
            }
        }
    };

    private static object Row(
        string merchant,
        decimal amount,
        DateOnly? date = null,
        DateOnly? authorized = null,
        string status = "New") => new
    {
        id = Guid.NewGuid(),
        date = date ?? new DateOnly(2026, 3, 9),
        amount,
        currency = "GBP",
        description = "CARD PAYMENT",
        merchantName = merchant,
        providerCategory = "FOOD_AND_DRINK",
        providerCategoryDetailed = (string?)null,
        authorizedDate = authorized,
        paymentChannel = "in store",
        city = (string?)null,
        logoUrl = (string?)null,
        pending = false,
        status,
        transactionId = (Guid?)null,
        removedAt = (DateTimeOffset?)null,
        accountName = "Current account",
        institutionName = "Monzo"
    };

    private static object Page(params object[] items) => new
    {
        items, page = 1, pageSize = 20, totalCount = items.Length
    };

    private static object Transaction(string name, decimal amount, Guid? groupId) => new
    {
        id = Guid.NewGuid(), name, description = (string?)null, amount,
        dateTime = DateTimeOffset.UtcNow, groupId,
        groupName = groupId is null ? null : "The flat",
        paidByUserId = Guid.NewGuid(), paidByUserName = "Anabel",
        categoryId = (Guid?)null, category = (string?)null
    };
}

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

    // ---- the same expense arriving twice ---------------------------------------------

    [Fact]
    public async Task Inbox_list_warns_on_the_row_a_suggestion_hangs_on()
    {
        _api.Returns("/api/inbox", Page(Row("Trattoria", 46.00m, matches: [Match("Dinner", 40m)])));

        var result = await Cli.RunAsync("inbox", "list", "--output", "text");

        // Filing this row plainly would be refused, which is a worse way to find out --
        // and the listing already carries the suggestion, so saying so costs nothing.
        Assert.Contains("(duplicate?)", result.Stdout);
        Assert.Contains("inbox matches", result.Stdout);
    }

    [Fact]
    public async Task Inbox_list_says_nothing_about_duplicates_when_there_are_none()
    {
        _api.Returns("/api/inbox", Page(Row("Sainsbury's", 42.10m)));

        var result = await Cli.RunAsync("inbox", "list", "--output", "text");

        Assert.DoesNotContain("duplicate", result.Stdout);
    }

    [Fact]
    public async Task Inbox_matches_names_the_expense_and_both_ways_to_answer()
    {
        var id = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _api.Returns($"/api/inbox/{id}/matches", new[] { Match("Dinner", 40m, transactionId) });

        var result = await Cli.RunAsync("inbox", "matches", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(transactionId.ToString(), result.Stdout);
        Assert.Contains("Dinner", result.Stdout);
        // Why it was offered, rather than asking anyone to take the suggestion on trust.
        Assert.Contains("2 days", result.Stdout);
        Assert.Contains("6.00 apart", result.Stdout);
        Assert.Contains("inbox link", result.Stdout);
        Assert.Contains("inbox dismiss-match", result.Stdout);
    }

    [Fact]
    public async Task Inbox_matches_says_so_when_nothing_looks_like_the_row()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/inbox/{id}/matches", Array.Empty<object>());

        var result = await Cli.RunAsync("inbox", "matches", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains($"inbox file {id}", result.Stdout);
    }

    [Fact]
    public async Task Filing_a_row_that_looks_like_a_duplicate_is_refused_with_the_matches_listed()
    {
        var id = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _api.Problem($"/api/inbox/{id}/file", 409, "POSSIBLE_DUPLICATE_EXPENSE",
            "This looks like an expense you have already recorded.",
            new { Matches = new[] { Match("Dinner", 40m, transactionId) } });

        var result = await Cli.RunAsync("inbox", "file", id.ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("POSSIBLE_DUPLICATE_EXPENSE", result.Error.GetProperty("code").GetString());

        // The id is what both answers take, and the refusal is the only other place it
        // appears -- without it a caller is told "no" and given nothing to act on.
        var details = result.Error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString()!).ToList();

        Assert.Contains(details, detail => detail.StartsWith(transactionId.ToString()));
        Assert.Contains(details, detail => detail.Contains("Dinner"));

        var remediation = result.Error.GetProperty("remediation").GetString()!;
        Assert.Contains("inbox link", remediation);
        Assert.Contains("--file-anyway", remediation);
        Assert.Contains("inbox dismiss-match", remediation);
    }

    [Fact]
    public async Task File_anyway_is_only_sent_when_it_was_asked_for()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/inbox/{id}/file", Transaction("Trattoria", 46.00m, null), status: 201);

        await Cli.RunAsync("inbox", "file", id.ToString());
        await Cli.RunAsync("inbox", "file", id.ToString(), "--file-anyway");

        var requests = _api.Requests.Where(r => r.Path == $"/api/inbox/{id}/file").ToList();

        // The refusal is what stops a second expense existing before anybody was told, so
        // the flag defaulting true for convenience would undo the whole feature.
        Assert.False(requests[0].Json.GetProperty("fileAnyway").GetBoolean());
        Assert.True(requests[1].Json.GetProperty("fileAnyway").GetBoolean());
    }

    [Fact]
    public async Task Inbox_link_attaches_the_row_and_says_the_expense_did_not_change()
    {
        var id = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _api.Returns($"/api/inbox/{id}/link", Transaction("Dinner", 40.00m, Guid.NewGuid()));

        var result = await Cli.RunAsync(
            "inbox", "link", id.ToString(), transactionId.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/inbox/{id}/link").Json;
        Assert.Equal(transactionId, body.GetProperty("transactionId").GetGuid());

        // What somebody wrote down stays what they wrote down: a card settling for more is
        // not a correction, and the command should not imply one was made.
        Assert.Contains("Nothing about the expense changed", result.Stdout);
    }

    [Fact]
    public async Task Inbox_dismiss_match_sends_the_pair_and_needs_no_confirmation()
    {
        var id = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _api.NoContent($"/api/inbox/{id}/dismiss-match");

        var result = await Cli.RunAsync(
            "inbox", "dismiss-match", id.ToString(), transactionId.ToString());

        // It removes a suggestion, not a record: the row is still there to file or ignore.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("dismissed", result.Json.GetProperty("status").GetString());

        var body = _api.Requests.Single(r => r.Path == $"/api/inbox/{id}/dismiss-match").Json;
        Assert.Equal(transactionId, body.GetProperty("transactionId").GetGuid());
    }

    [Fact]
    public async Task Transactions_bank_matches_answers_the_question_from_the_other_end()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}/bank-matches", new[] { Row("Trattoria", 46.00m) });

        var result = await Cli.RunAsync("tx", "bank-matches", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Trattoria", result.Stdout);
        // The expense id is already known here, so the suggested commands carry it.
        Assert.Contains($"inbox link <row-id> {id}", result.Stdout);
    }

    [Fact]
    public async Task Transactions_bank_matches_is_empty_without_error_when_nothing_matches()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}/bank-matches", Array.Empty<object>());

        var result = await Cli.RunAsync("tx", "bank-matches", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("No imported row", result.Stdout);
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

    /// <summary>
    /// One suggested expense. The defaults are the ordinary case the feature exists for: a
    /// card charge that posted two days after the meal and settled six higher, because a
    /// tip was added after the receipt was written.
    /// </summary>
    private static object Match(string name, decimal amount, Guid? transactionId = null) => new
    {
        transactionId = transactionId ?? Guid.NewGuid(),
        name,
        amount,
        currency = "GBP",
        dateTime = DateTimeOffset.UtcNow.AddDays(-2),
        groupId = Guid.NewGuid(),
        groupName = "The flat",
        paidByUserName = "Anabel",
        amountDifference = 6.00m,
        daysApart = 2
    };

    private static object Row(
        string merchant,
        decimal amount,
        DateOnly? date = null,
        DateOnly? authorized = null,
        string status = "New",
        object[]? matches = null) => new
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
        institutionName = "Monzo",
        possibleDuplicates = matches ?? []
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

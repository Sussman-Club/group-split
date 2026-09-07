using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The whole CLI, driven the way a caller drives it: real command tree, real generated
/// client, real HTTP, captured stdout and stderr. These assert the contract the docs
/// promise -- result on stdout, envelope on stderr, exit code says which -- rather than any
/// internal arrangement, so a refactor that keeps the promise keeps these passing.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class CommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public CommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    // ---- the output contract ---------------------------------------------------------

    [Fact]
    public async Task A_result_goes_to_stdout_and_nothing_else_does()
    {
        _api.Returns("/api/groups", new[] { Group("Trip") });

        var result = await Cli.RunAsync("groups", "list");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal("Trip", result.Json[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Output_is_json_when_stdout_is_not_a_terminal_without_being_asked()
    {
        _api.Returns("/api/groups", Array.Empty<object>());

        // No --json anywhere: the default has to be the machine-readable one off a terminal,
        // or every caller has to remember a flag.
        var result = await Cli.RunAsync("groups", "list");

        Assert.Equal(JsonValueKind.Array, result.Json.ValueKind);
    }

    [Fact]
    public async Task Text_output_renders_a_table_instead()
    {
        _api.Returns("/api/groups", new[] { Group("Trip") });

        var result = await Cli.RunAsync("groups", "list", "--output", "text");

        Assert.Contains("Trip", result.Stdout);
        Assert.Contains("Members", result.Stdout);
        Assert.ThrowsAny<JsonException>(() => result.Json);
    }

    [Fact]
    public async Task Fields_narrows_the_payload()
    {
        _api.Returns("/api/groups", new[] { Group("Trip") });

        var result = await Cli.RunAsync("groups", "list", "--json", "--fields", "name");

        Assert.True(result.Json[0].TryGetProperty("name", out _));
        Assert.False(result.Json[0].TryGetProperty("memberCount", out _));
    }

    // ---- errors ----------------------------------------------------------------------

    [Fact]
    public async Task An_api_failure_becomes_the_envelope_with_the_api_code_and_trace_id()
    {
        var id = Guid.NewGuid();
        _api.Problem($"/api/groups/{id}", 404, "GROUP_NOT_FOUND", "No group with that id.");

        var result = await Cli.RunAsync("groups", "show", id.ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal("GROUP_NOT_FOUND", result.Error.GetProperty("code").GetString());
        Assert.Equal("No group with that id.", result.Error.GetProperty("error").GetString());
        Assert.Equal("00-testtrace-0001-01", result.Error.GetProperty("traceId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.Error.GetProperty("remediation").GetString()));
    }

    [Fact]
    public async Task A_401_is_exit_code_2_and_says_how_to_sign_in()
    {
        _api.Problem("/api/users/me", 401, "UNAUTHENTICATED", "Unauthorized");

        var result = await Cli.RunAsync("users", "me");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Contains("auth login", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task Validation_messages_reach_the_envelope_as_details()
    {
        _api.Problem("/api/transactions", 400, "VALIDATION_FAILED", "Validation failed",
            new { Errors = new Dictionary<string, string[]> { ["Name"] = ["Name is required."] } });

        var result = await Cli.RunAsync("transactions", "create", "x", "1.00");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains(
            "Name: Name is required.",
            result.Error.GetProperty("details").EnumerateArray().Select(d => d.GetString()));
    }

    [Fact]
    public async Task A_usage_mistake_is_exit_code_3_and_points_at_the_right_help()
    {
        var result = await Cli.RunAsync("groups", "nosuchcommand");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal(ErrorCodes.Usage, result.Error.GetProperty("code").GetString());
        Assert.Contains("groups --help", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task A_bad_output_format_is_reported_rather_than_thrown()
    {
        // The reporting path reads --output to decide how to print the error, so the option
        // that failed used to be read while failing, and the process died with exit 134
        // instead of saying what was wrong.
        var result = await Cli.RunAsync("-o", "bogus", "config", "path");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal(ErrorCodes.Usage, result.Error.GetProperty("code").GetString());
        Assert.Contains("bogus", result.Error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_rather_than_thrown()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, "http://127.0.0.1:1/api");

        var result = await Cli.RunAsync("groups", "list");

        Assert.NotEqual(ExitCodes.Success, result.ExitCode);
        Assert.Equal(ErrorCodes.ServerUnreachable, result.Error.GetProperty("code").GetString());
    }

    // ---- auth ------------------------------------------------------------------------

    [Fact]
    public async Task The_token_from_the_environment_is_sent_as_the_bearer()
    {
        _api.Returns("/api/groups", Array.Empty<object>());

        await Cli.RunAsync("groups", "list");

        Assert.Equal("Bearer test-token", _api.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Without_any_credentials_nothing_is_sent_and_the_exit_code_says_to_sign_in()
    {
        _environment.Set(EnvironmentVariables.Token, null);
        _environment.Set(EnvironmentVariables.Authority, "https://example.com/realms/group-split");

        var result = await Cli.RunAsync("groups", "list");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal(ErrorCodes.AuthRequired, result.Error.GetProperty("code").GetString());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Auth_status_answers_rather_than_failing_when_signed_out()
    {
        _environment.Set(EnvironmentVariables.Token, null);

        var result = await Cli.RunAsync("auth", "status");

        // A question, not an error: a caller asking "am I signed in" should get an answer.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("signed_out", result.Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Auth_status_reports_a_token_supplied_by_the_environment()
    {
        var result = await Cli.RunAsync("auth", "status");

        Assert.Equal("token_from_environment", result.Json.GetProperty("status").GetString());
        Assert.Equal(EnvironmentVariables.Token, result.Json.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Auth_token_prints_the_token_raw_so_it_can_be_piped()
    {
        var result = await Cli.RunAsync("auth", "token");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("test-token", result.Stdout.Trim());
    }

    // ---- confirmation ----------------------------------------------------------------

    [Fact]
    public async Task A_mutation_without_a_terminal_exits_4_and_describes_what_it_would_do()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        var result = await Cli.RunAsync("transactions", "delete", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);

        var envelope = result.Json;
        Assert.True(envelope.GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal("transactions.delete", envelope.GetProperty("action").GetString());
        Assert.NotEmpty(envelope.GetProperty("changes").EnumerateArray());

        // The point of the envelope: a caller with no terminal re-runs this verbatim
        // instead of reconstructing the invocation.
        Assert.Equal(
            $"groupsplit transactions delete {id} --yes",
            envelope.GetProperty("confirmCommand").GetString());

        // Nothing was deleted -- only the read happened.
        Assert.DoesNotContain(_api.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task The_same_mutation_with_yes_goes_through()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        var result = await Cli.RunAsync("transactions", "delete", id.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_api.Requests, r => r.Method == "DELETE");
    }

    // ---- the rest of the surface -----------------------------------------------------

    [Fact]
    public async Task Groups_show_renders_one_group()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/groups/{id}", Group("Trip"));

        var result = await Cli.RunAsync("groups", "show", id.ToString());

        Assert.Equal("Trip", result.Json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Groups_create_sends_the_name_it_was_given()
    {
        _api.Returns("/api/groups", Group("New"));

        var result = await Cli.RunAsync("groups", "create", "Trip to Lisbon");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Asserted on the body, not just that a POST happened: the stub answers the same
        // either way, so a name that never left would look exactly like success.
        var request = _api.Requests.Single(r => r.Method == "POST" && r.Path == "/api/groups");
        Assert.Equal("Trip to Lisbon", request.Json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Transactions_create_sends_every_field_it_was_given()
    {
        var groupId = Guid.NewGuid();
        var payer = Guid.NewGuid();
        var category = Guid.NewGuid();

        _api.Returns("/api/transactions", Transaction("Dinner", 42.50m, groupId, "Trip"));

        var result = await Cli.RunAsync(
            "transactions", "create", "Dinner", "42.50",
            "--group", groupId.ToString(),
            "--paid-by", payer.ToString(),
            "--category-id", category.ToString(),
            "--description", "Birthday",
            "--date", "2026-03-04T18:30:00Z");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Method == "POST" && r.Path == "/api/transactions").Json;

        Assert.Equal("Dinner", body.GetProperty("name").GetString());
        Assert.Equal(42.50m, body.GetProperty("amount").GetDecimal());
        Assert.Equal(groupId, body.GetProperty("groupId").GetGuid());
        Assert.Equal(payer, body.GetProperty("paidByUserId").GetGuid());
        Assert.Equal(category, body.GetProperty("categoryId").GetGuid());
        Assert.Equal("Birthday", body.GetProperty("description").GetString());
        Assert.StartsWith("2026-03-04", body.GetProperty("dateTime").GetString());
    }

    [Fact]
    public async Task Transactions_create_defaults_the_date_rather_than_sending_nothing()
    {
        _api.Returns("/api/transactions", Transaction("Coffee", 3m, Guid.NewGuid(), "Trip"));

        await Cli.RunAsync("transactions", "create", "Coffee", "3.00");

        var body = _api.Requests.Single(r => r.Method == "POST").Json;

        // The API requires a date; omitting it would be a validation failure the user did
        // nothing to cause.
        Assert.NotEqual(default, body.GetProperty("dateTime").GetDateTimeOffset());
    }

    [Fact]
    public async Task Transactions_preview_does_not_create_anything()
    {
        _api.Returns("/api/transactions/preview", new
        {
            ruleName = "Evenly",
            splits = new[] { new { userId = Guid.NewGuid(), userName = "Anabel", amount = 21.25m } }
        });

        var result = await Cli.RunAsync(
            "transactions", "create", "Dinner", "42.50", "--preview", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Evenly", result.Stdout);
        Assert.Contains("21.25", result.Stdout);
        Assert.DoesNotContain(_api.Requests, r => r.Path == "/api/transactions");
    }

    [Fact]
    public async Task Transactions_list_returns_the_rows_it_was_given()
    {
        var groupId = Guid.NewGuid();

        _api.Returns("/api/transactions", Page(
            Transaction("Dinner", 42.50m, groupId, "Trip"),
            Transaction("Taxi", 18m, groupId, "Trip")));

        var result = await Cli.RunAsync("transactions", "list");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var items = result.Json.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Dinner", items[0].GetProperty("name").GetString());
        Assert.Equal(42.50m, items[0].GetProperty("amount").GetDecimal());
        Assert.Equal("Anabel", items[0].GetProperty("paidByUserName").GetString());
        Assert.Equal("Trip", items[0].GetProperty("groupName").GetString());
        Assert.Equal(2, result.Json.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Transactions_list_renders_a_table_with_the_totals_underneath()
    {
        _api.Returns("/api/transactions", Page(
            Transaction("Dinner", 42.50m, Guid.NewGuid(), "Trip")));

        var result = await Cli.RunAsync("transactions", "list", "--output", "text");

        Assert.Contains("Dinner", result.Stdout);
        Assert.Contains("42.50", result.Stdout);
        Assert.Contains("Anabel", result.Stdout);
        Assert.Contains("Trip", result.Stdout);
        // The paging footer: without it a truncated list looks like the whole of it.
        Assert.Contains("1 total", result.Stdout);
    }

    [Fact]
    public async Task Transactions_list_actually_sends_every_filter_it_was_given()
    {
        _api.Returns("/api/transactions", Page());

        var groupId = Guid.NewGuid();

        await Cli.RunAsync(
            "transactions", "list",
            "--group", groupId.ToString(),
            "--search", "pizza",
            "--category", "Food",
            "--from", "2026-01-01",
            "--to", "2026-02-01",
            "--page", "2",
            "--page-size", "5");

        // Asserted on the wire, because a filter that never leaves is indistinguishable
        // from one the server ignored: the command succeeds and returns the wrong rows.
        var request = _api.Requests.Single(r => r.Path == "/api/transactions");

        Assert.Equal(groupId.ToString(), request.Parameter("groupId"));
        Assert.Equal("pizza", request.Parameter("search"));
        Assert.Equal("Food", request.Parameter("category"));
        Assert.Equal("2", request.Parameter("page"));
        Assert.Equal("5", request.Parameter("pageSize"));
        Assert.StartsWith("2026-01-01", request.Parameter("from"));
        Assert.StartsWith("2026-02-01", request.Parameter("to"));
    }

    [Fact]
    public async Task Filters_that_were_not_given_are_not_sent_at_all()
    {
        _api.Returns("/api/transactions", Page());

        await Cli.RunAsync("transactions", "list", "--search", "pizza");

        var request = _api.Requests.Single(r => r.Path == "/api/transactions");

        // Null rather than empty: an empty `category=` is a filter on the empty string as
        // far as the API is concerned, and would match nothing.
        Assert.Null(request.Parameter("category"));
        Assert.Null(request.Parameter("groupId"));
        Assert.Null(request.Parameter("from"));
    }

    [Fact]
    public async Task Transactions_show_lists_the_split()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", new
        {
            id, name = "Dinner", description = "Birthday", amount = 42.50m,
            dateTime = DateTimeOffset.UtcNow, groupId = Guid.NewGuid(), groupName = "Trip",
            paidByUserId = Guid.NewGuid(), paidByUserName = "Anabel",
            categoryId = (Guid?)null, category = "Food",
            splits = new[]
            {
                new { userId = Guid.NewGuid(), userName = "Anabel", amount = 21.25m },
                new { userId = Guid.NewGuid(), userName = "Daniel", amount = 21.25m }
            }
        });

        var result = await Cli.RunAsync("transactions", "show", id.ToString(), "--output", "text");

        Assert.Contains("Dinner", result.Stdout);
        Assert.Contains("Daniel", result.Stdout);
        Assert.Contains("21.25", result.Stdout);
    }

    [Fact]
    public async Task Transactions_summary_totals_what_it_was_given()
    {
        _api.Returns("/api/transactions/summary", new { count = 7, total = 123.45m });

        var result = await Cli.RunAsync("transactions", "summary", "--output", "text");

        Assert.Contains("7", result.Stdout);
        Assert.Contains("123.45", result.Stdout);
    }

    [Fact]
    public async Task The_alias_tx_is_the_same_command()
    {
        _api.Returns("/api/transactions", Page(Transaction("Dinner", 42.50m, Guid.NewGuid(), "Trip")));

        Assert.Equal(1, (await Cli.RunAsync("tx", "list")).Json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Users_position_totals_across_groups()
    {
        _api.Returns("/api/users/me/position", new
        {
            net = 12.5m, owedToYou = 20m, youOwe = 7.5m,
            groups = new[] { new { groupId = Guid.NewGuid(), groupName = "Trip", balance = 12.5m, isArchive = false } }
        });

        var result = await Cli.RunAsync("users", "position", "--output", "text");

        Assert.Contains("Trip", result.Stdout);
    }

    [Fact]
    public async Task Invitations_list_is_empty_without_error_when_there_are_none()
    {
        _api.Returns("/api/invitations", Array.Empty<object>());

        var result = await Cli.RunAsync("invitations", "list", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("No pending invitations", result.Stdout);
    }

    [Fact]
    public async Task Categories_and_split_rules_list()
    {
        _api.Returns("/api/categories", new[]
        {
            new { id = Guid.NewGuid(), groupId = Guid.NewGuid(), name = "Food", defaultSplitRuleId = (Guid?)null, defaultSplitRuleName = (string?)null }
        });
        _api.Returns("/api/split-rules", new[] { new { id = Guid.NewGuid(), name = "Evenly" } });

        Assert.Equal("Food", (await Cli.RunAsync("categories", "list")).Json[0].GetProperty("name").GetString());
        Assert.Equal("Evenly", (await Cli.RunAsync("split-rules", "list")).Json[0].GetProperty("name").GetString());
    }

    private static object Group(string name) => new
    {
        id = Guid.NewGuid(), name, memberCount = 3, isArchive = false
    };

    private static object Page(params object[] items) => new
    {
        items, page = 1, pageSize = 20, totalCount = items.Length
    };

    private static object Transaction(string name, decimal amount, Guid groupId, string groupName) => new
    {
        id = Guid.NewGuid(), name, description = (string?)null, amount,
        dateTime = DateTimeOffset.UtcNow, groupId, groupName,
        paidByUserId = Guid.NewGuid(), paidByUserName = "Anabel",
        categoryId = (Guid?)null, category = (string?)null
    };

    private static object Transaction(string name) => new
    {
        id = Guid.NewGuid(), name, description = (string?)null, amount = 12.34m,
        dateTime = DateTimeOffset.UtcNow, groupId = (Guid?)null, groupName = (string?)null,
        paidByUserId = Guid.NewGuid(), paidByUserName = "Anabel",
        categoryId = (Guid?)null, category = (string?)null, splits = Array.Empty<object>()
    };
}

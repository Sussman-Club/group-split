using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The commands that write what divided an expense, rather than what it was divided into.
/// </summary>
/// <remarks>
/// Three surfaces, one promise between them: none of them sends an amount. Writing a rule's
/// history states what the rule said and when; reattaching points expenses at the entry that
/// was in force when each was spent; <c>--hand-split</c> and <c>--divided-by</c> say the same
/// thing about one expense. A request body carrying shares would be the first sign one of
/// them had started moving money, so these assert on the bodies rather than on exit codes.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class ProvenanceCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();
    private readonly List<string> _files = [];

    public ProvenanceCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }

        _api.Dispose();
        _environment.Dispose();
    }

    /// <summary>A history file on disk, since the command's whole input is one.</summary>
    private string AFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        _files.Add(path);

        return path;
    }

    private static string TwoEntries(Guid member) => $$"""
        [
          { "from": "2023-03-01", "definition": { "$type": "payer" } },
          {
            "from": "2024-01-01",
            "definition": { "$type": "shares", "shares": { "{{member}}": 2 } }
          }
        ]
        """;

    private static object History(Guid ruleId, Guid groupId) => new
    {
        id = ruleId,
        groupId,
        name = "Groceries",
        versions = new[]
        {
            new
            {
                id = Guid.NewGuid(),
                startedAt = DateTimeOffset.UtcNow,
                supersededAt = (DateTimeOffset?)null,
                definition = new Dictionary<string, object> { ["$type"] = "payer" }
            }
        }
    };

    // ---- split-rules versions set ----------------------------------------------------

    /// <summary>
    /// The read that was there before is still reachable the way it always was, which is the
    /// thing adding a subcommand under it could quietly have broken.
    /// </summary>
    [Fact]
    public async Task Versions_still_reads_a_rules_history_by_id()
    {
        var ruleId = Guid.NewGuid();
        _api.Returns($"/api/split-rules/{ruleId}/versions", History(ruleId, Guid.NewGuid()));

        var result = await Cli.RunAsync("split-rules", "versions", ruleId.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Groceries", result.Json.GetProperty("name").GetString());
        Assert.Equal("GET", _api.Requests.Single().Method);
    }

    [Fact]
    public async Task Versions_set_sends_the_file_as_the_body()
    {
        var ruleId = Guid.NewGuid();
        var member = Guid.NewGuid();
        _api.Returns($"/api/split-rules/{ruleId}/versions", History(ruleId, Guid.NewGuid()));

        var result = await Cli.RunAsync(
            "split-rules", "versions", "set", ruleId.ToString(),
            "--file", AFile(TwoEntries(member)), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var sent = _api.Requests.Single(request => request.Method == "PUT");
        var body = sent.Json;

        Assert.Equal(2, body.GetArrayLength());
        Assert.Equal("payer", body[0].GetProperty("definition").GetProperty("$type").GetString());
        Assert.Equal("shares", body[1].GetProperty("definition").GetProperty("$type").GetString());
    }

    /// <summary>
    /// A bare date means midnight UTC, not midnight wherever the command was typed.
    /// </summary>
    /// <remarks>
    /// These dates are the boundaries of the windows that decide which version an expense
    /// falls into, so an offset picked up from the machine would put a first-of-the-month
    /// expense on one side of the line in London and the other in New York. The test runs on
    /// whatever timezone the agent is on, which is exactly why it is worth having.
    /// </remarks>
    [Fact]
    public async Task A_date_with_no_offset_is_sent_as_midnight_utc()
    {
        var ruleId = Guid.NewGuid();
        _api.Returns($"/api/split-rules/{ruleId}/versions", History(ruleId, Guid.NewGuid()));

        await Cli.RunAsync(
            "split-rules", "versions", "set", ruleId.ToString(),
            "--file", AFile(TwoEntries(Guid.NewGuid())), "--yes");

        var body = _api.Requests.Single(request => request.Method == "PUT").Json;

        Assert.Equal(
            new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.Zero),
            body[0].GetProperty("from").GetDateTimeOffset());
    }

    /// <summary>
    /// It rewrites what a rule is recorded as having said, so it stops for a confirmation
    /// like every other destructive change.
    /// </summary>
    [Fact]
    public async Task Versions_set_without_yes_asks_first_and_writes_nothing()
    {
        var ruleId = Guid.NewGuid();

        var result = await Cli.RunAsync(
            "split-rules", "versions", "set", ruleId.ToString(),
            "--file", AFile(TwoEntries(Guid.NewGuid())));

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("split-rules.versions.set", result.Json.GetProperty("action").GetString());
        Assert.DoesNotContain(_api.Requests, request => request.Method == "PUT");
    }

    /// <summary>
    /// A dry run prints the chain and sends nothing at all -- not even the read a
    /// confirmation would need.
    /// </summary>
    [Fact]
    public async Task Versions_set_dry_run_prints_the_chain_and_sends_nothing()
    {
        var ruleId = Guid.NewGuid();

        var result = await Cli.RunAsync(
            "split-rules", "versions", "set", ruleId.ToString(),
            "--file", AFile(TwoEntries(Guid.NewGuid())), "--dry-run");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("dryRun", result.Json.GetProperty("status").GetString());
        Assert.Equal(2, result.Json.GetProperty("versions").GetArrayLength());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task A_history_file_that_is_not_there_is_a_usage_error()
    {
        var result = await Cli.RunAsync(
            "split-rules", "versions", "set", Guid.NewGuid().ToString(),
            "--file", Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"), "--yes");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task A_history_file_that_is_not_a_history_is_a_usage_error()
    {
        var result = await Cli.RunAsync(
            "split-rules", "versions", "set", Guid.NewGuid().ToString(),
            "--file", AFile("""{ "from": "2023-03-01" }"""), "--yes");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task An_empty_history_file_is_a_usage_error()
    {
        var result = await Cli.RunAsync(
            "split-rules", "versions", "set", Guid.NewGuid().ToString(),
            "--file", AFile("[]"), "--yes");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    // ---- transactions reattach -------------------------------------------------------

    private static object Summary(Guid groupId, bool dryRun) => new
    {
        groupId,
        dryRun,
        examined = 1411,
        changed = 733,
        leftWithoutAVersion = 12,
        byRule = new[]
        {
            new
            {
                splitRuleId = Guid.NewGuid(),
                splitRuleName = "Groceries",
                examined = 900,
                changed = 500,
                uncovered = 12
            }
        }
    };

    [Fact]
    public async Task Reattach_sends_the_group_and_renders_the_breakdown()
    {
        var groupId = Guid.NewGuid();
        _api.Returns("/api/transactions/reattach", Summary(groupId, dryRun: false));

        var result = await Cli.RunAsync(
            "transactions", "reattach", "--group", groupId.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var sent = _api.Requests.Single(request => request.Path == "/api/transactions/reattach");

        Assert.Equal(groupId, sent.Json.GetProperty("groupId").GetGuid());
        Assert.False(sent.Json.GetProperty("dryRun").GetBoolean());

        Assert.Equal(733, result.Json.GetProperty("changed").GetInt32());
        Assert.Equal("Groceries", result.Json.GetProperty("byRule")[0].GetProperty("splitRuleName").GetString());
    }

    /// <summary>
    /// A dry run is a read and needs no confirmation, which is what makes it the thing to
    /// reach for first.
    /// </summary>
    [Fact]
    public async Task Reattach_dry_run_goes_through_without_a_confirmation()
    {
        var groupId = Guid.NewGuid();
        _api.Returns("/api/transactions/reattach", Summary(groupId, dryRun: true));

        var result = await Cli.RunAsync(
            "transactions", "reattach", "--group", groupId.ToString(), "--dry-run");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(_api.Requests.Single().Json.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task Reattach_without_yes_asks_first_and_writes_nothing()
    {
        var groupId = Guid.NewGuid();

        var result = await Cli.RunAsync("transactions", "reattach", "--group", groupId.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("transactions.reattach", result.Json.GetProperty("action").GetString());
        Assert.Empty(_api.Requests);
    }

    // ---- transactions update --hand-split / --divided-by -----------------------------

    private static object Transaction(Guid id) => new
    {
        id,
        name = "Groceries",
        description = (string?)null,
        amount = 90.00m,
        dateTime = DateTimeOffset.UtcNow,
        groupId = Guid.NewGuid(),
        groupName = "The flat",
        paidByUserId = Guid.NewGuid(),
        paidByUserName = "Anabel",
        categoryId = (Guid?)null,
        category = (string?)null
    };

    [Fact]
    public async Task Hand_split_records_that_the_shares_are_the_expenses_own()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction(id));
        _api.Returns($"/api/transactions/{id}/division-source", Transaction(id));

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--hand-split");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var sent = _api.Requests.Single(request =>
            request.Path == $"/api/transactions/{id}/division-source");

        Assert.Equal("PUT", sent.Method);
        Assert.Equal(JsonValueKind.Null, sent.Json.GetProperty("splitRuleVersionId").ValueKind);
    }

    [Fact]
    public async Task Divided_by_records_the_version_that_worked_the_shares_out()
    {
        var id = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction(id));
        _api.Returns($"/api/transactions/{id}/division-source", Transaction(id));

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--divided-by", versionId.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var sent = _api.Requests.Single(request =>
            request.Path == $"/api/transactions/{id}/division-source");

        Assert.Equal(versionId, sent.Json.GetProperty("splitRuleVersionId").GetGuid());
    }

    /// <summary>
    /// The edit and the record of what divided it are two requests, so a failure between
    /// them has to say which one landed.
    /// </summary>
    /// <remarks>
    /// Reported as the second call's own failure it reads as "nothing happened", and an
    /// operator would go looking for an edit that is already saved -- or, worse, leave it
    /// half applied because re-running looks reckless. It is not: the command reads the
    /// expense first and sends only what still differs, so the fields that already moved
    /// produce no operation the second time.
    /// </remarks>
    [Fact]
    public async Task A_failure_recording_what_divided_it_says_the_edit_itself_was_saved()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction(id));
        _api.Problem($"/api/transactions/{id}/division-source", 409,
            "SPLIT_RULE_VERSION_NOT_IN_GROUP", "That version belongs to another group's rule.");

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--amount", "120", "--hand-split");

        Assert.NotEqual(ExitCodes.Success, result.ExitCode);

        // The half that landed, and it carried the new amount.
        var saved = _api.Requests.Single(request => request.Method == "PATCH");
        Assert.Contains("120", saved.Body);

        Assert.Contains("edit was saved", result.Error.GetProperty("error").GetString()!);
        Assert.Contains("SPLIT_RULE_VERSION_NOT_IN_GROUP", result.Error.GetProperty("code").GetString()!);
        Assert.Contains("again", result.Error.GetProperty("remediation").GetString()!);
    }

    /// <summary>
    /// Neither of them ever sends a share. They record what produced the shares that are
    /// already there, and a body carrying amounts would mean one of them had started
    /// deciding instead.
    /// </summary>
    [Fact]
    public async Task Neither_flag_sends_a_single_share()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction(id));
        _api.Returns($"/api/transactions/{id}/division-source", Transaction(id));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--hand-split");

        Assert.DoesNotContain(_api.Requests, request =>
            request.Body.Contains("split", StringComparison.OrdinalIgnoreCase) &&
            request.Body.Contains("amount", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("--hand-split", "--divided-by")]
    [InlineData("--hand-split", "--split")]
    [InlineData("--hand-split", "--redivide")]
    [InlineData("--divided-by", "--split")]
    [InlineData("--divided-by", "--redivide")]
    public async Task Flags_that_contradict_each_other_are_refused_before_anything_is_sent(
        string first, string second)
    {
        var id = Guid.NewGuid();
        var member = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction(id));

        string[] Flag(string name) => name switch
        {
            "--divided-by" => [name, Guid.NewGuid().ToString()],
            "--split" => [name, $"{member}=90.00"],
            _ => [name]
        };

        var result = await Cli.RunAsync(
            ["transactions", "update", id.ToString(), .. Flag(first), .. Flag(second)]);

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }
}

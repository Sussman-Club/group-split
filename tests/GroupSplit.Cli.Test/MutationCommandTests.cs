using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Shared;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The commands that change something: edits, deletions, settlements, and the account
/// itself. Asserted on the request body rather than on the exit code, because the stub
/// answers the same whether or not a field was sent -- so an edit that quietly dropped the
/// value it was given looks exactly like one that worked.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class MutationCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    public MutationCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    // ---- transactions update ---------------------------------------------------------

    [Fact]
    public async Task Transactions_update_sends_only_the_fields_that_were_named()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--name", "Late dinner");

        var operations = Patch(id);

        // One operation, not a whole model: the endpoint reads the patch as well as
        // applying it, and naming the shares -- even to the values they already hold --
        // means "these amounts" rather than "divide it again".
        Assert.Equal(1, operations.GetArrayLength());
        Assert.Equal("replace", operations[0].GetProperty("op").GetString());
        // Compared without regard to case, here and by the API: the patch library derives
        // the pointer from the property, so it goes out PascalCase, and pinning that spelling
        // would make this a test of the library rather than of the command.
        Assert.Equal("/name", operations[0].GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal("Late dinner", operations[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Transactions_update_can_move_an_expense_onto_your_own_ledger()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--personal");

        var operation = Patch(id)[0];

        Assert.Equal("/groupId", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, operation.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Transactions_update_clears_a_description_that_was_given_as_empty()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--description", "");

        // An empty string is somebody clearing the note, and it has to be told apart from
        // not passing the flag at all.
        var operation = Patch(id)[0];

        Assert.Equal("/description", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(string.Empty, operation.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Transactions_update_sends_the_shares_as_one_operation_on_splits()
    {
        var id = Guid.NewGuid();
        var anabel = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--split", $"{anabel}=42.50");

        var operation = Patch(id)[0];

        Assert.Equal("/splits", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(anabel, operation.GetProperty("value")[0].GetProperty("userId").GetGuid());
    }

    [Theory]
    [InlineData("--group", "--personal")]
    [InlineData("--category-id", "--no-category")]
    public async Task Contradictory_flags_are_refused_before_anything_is_sent(string set, string clear)
    {
        var result = await Cli.RunAsync(
            "transactions", "update", Guid.NewGuid().ToString(), set, Guid.NewGuid().ToString(), clear);

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("contradict", result.Error.GetProperty("error").GetString());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task An_update_that_names_nothing_says_so_rather_than_sending_an_empty_patch()
    {
        var result = await Cli.RunAsync("transactions", "update", Guid.NewGuid().ToString());

        // An empty patch is a successful no-op on the server, which would report success
        // for a command that did nothing the caller asked for.
        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    // ---- groups ----------------------------------------------------------------------

    [Fact]
    public async Task Groups_rename_sends_one_replace_on_the_name()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/groups/{id}", Group("Trip to Lisbon"));

        var result = await Cli.RunAsync("groups", "rename", id.ToString(), "Trip to Lisbon");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var operations = _api.Requests.Single(r => r.Method == "PATCH").Json;
        Assert.Equal("/name", operations[0].GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal("Trip to Lisbon", operations[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Groups_settle_sends_the_amount_and_the_direction_it_was_given()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members", new[] { Member(omar, "Omar") });
        _api.NoContent($"/api/groups/{id}/settle");

        var result = await Cli.RunAsync(
            "groups", "settle", id.ToString(), omar.ToString(), "40.00",
            "--direction", "youpaidthem", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/settle").Json;

        Assert.Equal(omar, body.GetProperty("userId").GetGuid());
        Assert.Equal(40.00m, body.GetProperty("amount").GetDecimal());
        // Stated rather than inferred: a debtor recording their own repayment acts
        // precisely when the balance still says they owe.
        Assert.Equal(
            SettlementDirection.YouPaidThem,
            (SettlementDirection)body.GetProperty("direction").GetInt32());
    }

    [Fact]
    public async Task Groups_settle_defaults_to_the_creditor_s_side()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members", new[] { Member(omar, "Omar") });
        _api.NoContent($"/api/groups/{id}/settle");

        await Cli.RunAsync("groups", "settle", id.ToString(), omar.ToString(), "40.00", "--yes");

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/settle").Json;
        Assert.Equal(
            SettlementDirection.TheyPaidYou,
            (SettlementDirection)body.GetProperty("direction").GetInt32());
    }

    [Fact]
    public async Task Groups_settle_sends_the_date_and_the_note()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members", new[] { Member(omar, "Omar") });
        _api.NoContent($"/api/groups/{id}/settle");

        await Cli.RunAsync(
            "groups", "settle", id.ToString(), omar.ToString(), "40.00",
            "--date", "2026-09-30", "--note", "cash", "--yes");

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/settle").Json;

        Assert.Equal(new DateTime(2026, 9, 30), body.GetProperty("date").GetDateTimeOffset().Date);
        Assert.Equal("cash", body.GetProperty("description").GetString());
    }

    // ---- groups settle-up ------------------------------------------------------------

    /// <summary>
    /// The end-of-the-month case. The scope goes up as a filter, which is what keeps a new
    /// way of narrowing a settling-up from needing a new command.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_sends_the_scope_it_was_given()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/settle-up/preview", Preview());
        _api.Returns($"/api/groups/{id}/settle-up", Run());

        var result = await Cli.RunAsync(
            "groups", "settle-up", id.ToString(),
            "--to", "2026-09-30", "--label", "September", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/settle-up").Json;

        Assert.Equal(
            new DateTime(2026, 9, 30),
            body.GetProperty("scope").GetProperty("to").GetDateTimeOffset().Date);

        Assert.Equal("September", body.GetProperty("label").GetString());
    }

    /// <summary>
    /// With nothing said, it settles everything outstanding -- the one-tap case, which has
    /// to reach the server as a scope that narrows nothing rather than as no request at all.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_with_no_scope_settles_everything()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/settle-up/preview", Preview());
        _api.Returns($"/api/groups/{id}/settle-up", Run());

        await Cli.RunAsync("groups", "settle-up", id.ToString(), "--yes");

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/settle-up").Json;
        var scope = body.GetProperty("scope");

        Assert.Equal(JsonValueKind.Null, scope.GetProperty("to").ValueKind);
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("category").ValueKind);
    }

    /// <summary>
    /// A dry run previews and stops. Nothing may reach the endpoint that records one.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_dry_run_writes_nothing()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/settle-up/preview", Preview());
        _api.Returns($"/api/groups/{id}/settle-up", Run());

        var result = await Cli.RunAsync("groups", "settle-up", id.ToString(), "--dry-run");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        Assert.Contains(_api.Requests, r => r.Path == $"/api/groups/{id}/settle-up/preview");
        Assert.DoesNotContain(_api.Requests, r => r.Path == $"/api/groups/{id}/settle-up");
    }

    /// <summary>
    /// Previewed before it is confirmed, so what somebody agrees to is the payments
    /// themselves. Without the preview the confirmation could only describe them.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_without_a_terminal_lists_the_payments_it_would_write()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/settle-up/preview", Preview());

        var result = await Cli.RunAsync("groups", "settle-up", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);

        var changes = result.Json.GetProperty("changes").EnumerateArray()
            .Select(change => change.GetString())
            .ToList();

        Assert.Contains("Omar pays Daniel 40.00.", changes);
    }

    /// <summary>
    /// Undoing is what makes a settled expense editable again, so the confirmation has to be
    /// clear that it is not a refund: the payments stay where they are.
    /// </summary>
    [Fact]
    public async Task Groups_undo_settle_up_reopens_the_run_it_was_given()
    {
        var id = Guid.NewGuid();
        var run = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/settlements/{run}/reopen", Run());

        var result = await Cli.RunAsync("groups", "undo-settle-up", id.ToString(), run.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_api.Requests, r => r.Path == $"/api/groups/{id}/settlements/{run}/reopen");
    }

    [Fact]
    public async Task Groups_undo_settle_up_without_a_terminal_says_no_money_moves()
    {
        var id = Guid.NewGuid();
        var run = Guid.NewGuid();

        var result = await Cli.RunAsync("groups", "undo-settle-up", id.ToString(), run.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);

        var changes = result.Json.GetProperty("changes").EnumerateArray()
            .Select(change => change.GetString() ?? "")
            .ToList();

        Assert.Contains(changes, change => change.Contains("No money moves"));
        Assert.Empty(_api.Requests);
    }

    private static object Preview() => new
    {
        payments = new[]
        {
            new
            {
                fromUserId = Guid.NewGuid(),
                fromUserName = "Omar",
                toUserId = Guid.NewGuid(),
                toUserName = "Daniel",
                amount = 40.00m
            }
        },
        balances = Array.Empty<object>(),
        transactionCount = 3,
        total = 120.00m,
        coversFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        coversTo = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        suggestedLabel = "September 2026"
    };

    private static object Run() => new
    {
        id = Guid.NewGuid(),
        label = "September 2026",
        ranAt = DateTimeOffset.UtcNow,
        effectiveDate = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        payments = new[]
        {
            new
            {
                fromUserId = Guid.NewGuid(),
                fromUserName = "Omar",
                toUserId = Guid.NewGuid(),
                toUserName = "Daniel",
                amount = 40.00m
            }
        },
        transactionCount = 4,
        total = 120.00m
    };

    [Fact]
    public async Task Groups_settle_without_a_terminal_names_the_member_in_the_confirmation()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members", new[] { Member(omar, "Omar") });

        var result = await Cli.RunAsync("groups", "settle", id.ToString(), omar.ToString(), "40.00");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("Omar", result.Json.GetProperty("summary").GetString());
        Assert.DoesNotContain(_api.Requests, r => r.Path == $"/api/groups/{id}/settle");
    }

    [Fact]
    public async Task Groups_remove_member_names_the_person_rather_than_echoing_a_guid()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members", new[] { Member(omar, "Omar") });

        var result = await Cli.RunAsync("groups", "remove-member", id.ToString(), omar.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("Omar", result.Json.GetProperty("summary").GetString());
        Assert.Equal(
            $"groupsplit groups remove-member {id} {omar} --yes",
            result.Json.GetProperty("confirmCommand").GetString());
    }

    [Fact]
    public async Task Groups_activity_shows_transfers_beside_expenses()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/activity", new
        {
            items = new object[]
            {
                new
                {
                    id = Guid.NewGuid(), kind = (int)ActivityKind.Transfer, name = "Repayment",
                    description = (string?)null, amount = 40m, dateTime = DateTimeOffset.UtcNow,
                    paidByUserId = Guid.NewGuid(), paidByUserName = "Omar",
                    paidToUserId = Guid.NewGuid(), paidToUserName = "Anabel",
                    category = (string?)null
                }
            },
            page = 1, pageSize = 20, totalCount = 1
        });

        var result = await Cli.RunAsync("groups", "activity", id.ToString(), "--output", "text");

        // The one listing that can see a transfer at all, which is why it is not
        // `transactions list --group`.
        Assert.Contains("transfer", result.Stdout);
        Assert.Contains("Omar", result.Stdout);
        Assert.Contains("Anabel", result.Stdout);
    }

    [Fact]
    public async Task Groups_invitations_lists_the_addresses_a_group_is_waiting_on()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/invitations", new[]
        {
            new
            {
                id = Guid.NewGuid(), groupId = id, groupName = "The flat",
                email = "omar@example.com", invitedByUserName = "Anabel",
                invitedAt = DateTimeOffset.UtcNow
            }
        });

        var result = await Cli.RunAsync("groups", "invitations", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("omar@example.com", result.Stdout);
    }

    [Fact]
    public async Task Groups_withdraw_invitation_needs_no_confirmation_because_it_can_be_sent_again()
    {
        var id = Guid.NewGuid();
        var invitation = Guid.NewGuid();

        _api.NoContent($"/api/groups/{id}/invitations/{invitation}");

        var result = await Cli.RunAsync(
            "groups", "withdraw-invitation", id.ToString(), invitation.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("withdrawn", result.Json.GetProperty("status").GetString());
        Assert.Contains(_api.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task Groups_balances_shows_what_to_pay_and_not_only_how_it_stands()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/balances", new
        {
            netBalances = new[]
            {
                new
                {
                    userId = Guid.NewGuid(), userName = "Anabel",
                    amountPaid = 60m, amountOwed = 20m, balance = 40m
                }
            },
            owedToYou = new[] { new { userId = Guid.NewGuid(), userName = "Omar", amount = 40m } },
            youOwed = Array.Empty<object>()
        });

        var result = await Cli.RunAsync("groups", "balances", id.ToString(), "--output", "text");

        Assert.Contains("Owed to you", result.Stdout);
        Assert.Contains("Omar", result.Stdout);
    }

    // ---- categories ------------------------------------------------------------------

    [Fact]
    public async Task Categories_create_sends_the_group_and_the_rule_it_was_given()
    {
        var groupId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();

        _api.Returns("/api/categories", Category("Groceries", groupId));

        var result = await Cli.RunAsync(
            "categories", "create", "Groceries",
            "--group", groupId.ToString(), "--rule", ruleId.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Method == "POST").Json;
        Assert.Equal("Groceries", body.GetProperty("name").GetString());
        Assert.Equal(groupId, body.GetProperty("groupId").GetGuid());
        Assert.Equal(ruleId, body.GetProperty("defaultSplitRuleId").GetGuid());
    }

    [Fact]
    public async Task Categories_create_without_a_group_is_a_usage_error()
    {
        var result = await Cli.RunAsync("categories", "create", "Groceries");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal(ErrorCodes.Usage, result.Error.GetProperty("code").GetString());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Categories_update_keeps_the_name_it_was_not_asked_to_change()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();

        _api.Returns("/api/categories", new[] { Category("Groceries", groupId, id) });
        _api.Returns($"/api/categories/{id}", Category("Groceries", groupId, id));

        await Cli.RunAsync("categories", "update", id.ToString(), "--rule", ruleId.ToString());

        // The endpoint is a PUT and its request requires a name, so an update that says
        // nothing about the name must not send a blank one.
        var body = _api.Requests.Single(r => r.Method == "PUT").Json;
        Assert.Equal("Groceries", body.GetProperty("name").GetString());
        Assert.Equal(ruleId, body.GetProperty("defaultSplitRuleId").GetGuid());
    }

    [Fact]
    public async Task Categories_update_can_clear_the_rule()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        _api.Returns("/api/categories", new[] { Category("Groceries", groupId, id, Guid.NewGuid()) });
        _api.Returns($"/api/categories/{id}", Category("Groceries", groupId, id));

        await Cli.RunAsync("categories", "update", id.ToString(), "--no-rule");

        var body = _api.Requests.Single(r => r.Method == "PUT").Json;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("defaultSplitRuleId").ValueKind);
    }

    [Fact]
    public async Task Categories_update_refuses_a_rule_and_no_rule_together()
    {
        var result = await Cli.RunAsync(
            "categories", "update", Guid.NewGuid().ToString(),
            "--rule", Guid.NewGuid().ToString(), "--no-rule");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Categories_delete_says_it_will_be_refused_while_expenses_are_filed_under_it()
    {
        var id = Guid.NewGuid();
        _api.Returns("/api/categories", new[] { Category("Groceries", Guid.NewGuid(), id) });

        var result = await Cli.RunAsync("categories", "delete", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("Groceries", result.Json.GetProperty("summary").GetString());
        Assert.Contains(
            result.Json.GetProperty("changes").EnumerateArray().Select(change => change.GetString()),
            change => change!.Contains("Refused"));
    }

    // ---- split rules -----------------------------------------------------------------

    [Fact]
    public async Task Split_rules_create_sends_the_kind_the_flags_described()
    {
        var groupId = Guid.NewGuid();
        var anabel = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns("/api/split-rules", Rule("By room size", groupId));

        var result = await Cli.RunAsync(
            "split-rules", "create", "By room size", "--group", groupId.ToString(),
            "--shares", $"{anabel}=3", "--shares", $"{omar}=2");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var definition = _api.Requests.Single(r => r.Method == "POST").Json.GetProperty("definition");

        // The discriminator is what tells the four kinds apart on the wire, so the flag
        // that was passed has to reach it.
        Assert.Equal("shares", definition.GetProperty("$type").GetString());
        Assert.Equal(3, definition.GetProperty("shares").GetProperty(anabel.ToString()).GetInt32());
    }

    [Fact]
    public async Task Split_rules_create_sends_an_even_rule_that_names_nobody()
    {
        var groupId = Guid.NewGuid();
        _api.Returns("/api/split-rules", Rule("Evenly", groupId));

        await Cli.RunAsync("split-rules", "create", "Evenly", "--group", groupId.ToString(), "--even");

        var definition = _api.Requests.Single(r => r.Method == "POST").Json.GetProperty("definition");

        // Empty rather than today's members: it keeps dividing evenly when somebody joins.
        Assert.Equal("even", definition.GetProperty("$type").GetString());
        Assert.Equal(0, definition.GetProperty("among").GetArrayLength());
    }

    [Fact]
    public async Task Split_rules_create_without_a_division_says_which_flags_would_give_one()
    {
        var result = await Cli.RunAsync(
            "split-rules", "create", "Evenly", "--group", Guid.NewGuid().ToString());

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("--even", result.Error.GetProperty("remediation").GetString());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Two_kinds_at_once_are_refused_rather_than_one_taking_precedence()
    {
        var result = await Cli.RunAsync(
            "split-rules", "create", "Muddle", "--group", Guid.NewGuid().ToString(),
            "--even", "--payer");

        // Picking for the caller would write the rule they did not mean, and a rule is read
        // by every expense afterwards.
        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Split_rules_update_keeps_the_division_when_only_the_name_changes()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/split-rules/{id}", Rule("By room size", Guid.NewGuid(), id, omar));

        await Cli.RunAsync("split-rules", "update", id.ToString(), "--name", "By floor area");

        var body = _api.Requests.Single(r => r.Method == "PUT").Json;

        Assert.Equal("By floor area", body.GetProperty("name").GetString());
        // Read back off the rule rather than sent blank: the endpoint is a PUT, and a
        // rename should not clear how it divides.
        Assert.Equal("shares", body.GetProperty("definition").GetProperty("$type").GetString());
        Assert.Equal(
            3,
            body.GetProperty("definition").GetProperty("shares").GetProperty(omar.ToString()).GetInt32());
    }

    [Fact]
    public async Task Split_rules_show_renders_the_division()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/split-rules/{id}", Rule("By room size", Guid.NewGuid(), id, Guid.NewGuid()));

        var result = await Cli.RunAsync("split-rules", "show", id.ToString(), "--output", "text");

        Assert.Contains("By room size", result.Stdout);
        Assert.Contains("whole shares", result.Stdout);
    }

    [Fact]
    public async Task Split_rules_delete_promises_that_recorded_expenses_keep_their_shares()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/split-rules/{id}", Rule("By room size", Guid.NewGuid(), id, Guid.NewGuid()));

        var result = await Cli.RunAsync("split-rules", "delete", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains(
            result.Json.GetProperty("changes").EnumerateArray().Select(change => change.GetString()),
            change => change!.Contains("keep the shares"));
    }

    // ---- the account itself ----------------------------------------------------------

    [Fact]
    public async Task Users_delete_needs_confirming_and_says_it_cannot_be_undone()
    {
        _api.Returns("/api/users/me", new
        {
            id = Guid.NewGuid(), fullName = "Anabel Benitez", email = "anabel@example.com"
        });

        var result = await Cli.RunAsync("users", "delete");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("users.delete", result.Json.GetProperty("action").GetString());
        Assert.Contains("anabel@example.com", result.Json.GetProperty("summary").GetString());
        Assert.Contains(
            result.Json.GetProperty("changes").EnumerateArray().Select(change => change.GetString()),
            change => change!.Contains("cannot be undone"));

        Assert.DoesNotContain(_api.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task An_unsettled_account_deletion_names_the_groups_standing_in_the_way()
    {
        _api.Returns("/api/users/me", new
        {
            id = Guid.NewGuid(), fullName = "Anabel Benitez", email = "anabel@example.com"
        });

        _api.Problem("/api/users/me", 409, "ACCOUNT_NOT_SETTLED",
            "Settle up in every group before deleting your account.",
            new
            {
                OutstandingBalances = new[]
                {
                    new { groupId = Guid.NewGuid(), groupName = "The flat", balance = -25.50m },
                    new { groupId = Guid.NewGuid(), groupName = "Trip to Lisbon", balance = 12m }
                }
            },
            // Only the DELETE: the command reads the account first, and that GET has to keep
            // working or the refusal under test is never reached.
            method: "DELETE");

        var result = await Cli.RunAsync("users", "delete", "--yes");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal("ACCOUNT_NOT_SETTLED", result.Error.GetProperty("code").GetString());

        // Without these the refusal reads "settle up in every group" with no way to learn
        // which ones, and the list is right there on the problem.
        var details = result.Error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString()).ToList();

        Assert.Contains("The flat: you owe 25.50", details);
        Assert.Contains("Trip to Lisbon: you are owed 12.00", details);
    }

    private System.Text.Json.JsonElement Patch(Guid transactionId)
        => _api.Requests.Single(r => r.Method == "PATCH" && r.Path == $"/api/transactions/{transactionId}").Json;

    private static object Group(string name) => new
    {
        id = Guid.NewGuid(), name, memberCount = 3, isArchive = false
    };

    /// <summary>
    /// A member as the API describes one. firstName and lastName, not fullName: the full
    /// name is computed and JsonIgnore'd, so a stub that sends it leaves every name blank
    /// and every confirmation nameless.
    /// </summary>
    private static object Member(Guid id, string name) => new
    {
        id, firstName = name, lastName = "Haddad", email = $"{name.ToLowerInvariant()}@example.com"
    };

    private static object Category(string name, Guid groupId, Guid? id = null, Guid? ruleId = null) => new
    {
        id = id ?? Guid.NewGuid(), groupId, name,
        defaultSplitRuleId = ruleId, defaultSplitRuleName = ruleId is null ? null : "By room size"
    };

    private static object Rule(string name, Guid groupId, Guid? id = null, Guid? member = null) => new
    {
        id = id ?? Guid.NewGuid(), groupId, name,
        definition = new Dictionary<string, object>
        {
            ["$type"] = "shares",
            ["shares"] = new Dictionary<string, int> { [(member ?? Guid.NewGuid()).ToString()] = 3 }
        }
    };

    private static object Transaction(string name) => new
    {
        id = Guid.NewGuid(), name, description = (string?)null, amount = 42.50m,
        dateTime = DateTimeOffset.UtcNow, groupId = Guid.NewGuid(), groupName = "The flat",
        paidByUserId = Guid.NewGuid(), paidByUserName = "Anabel",
        categoryId = (Guid?)null, category = (string?)null
    };
}

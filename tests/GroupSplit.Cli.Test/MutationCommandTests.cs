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

    /// <summary>
    /// <c>--redivide</c> sends the one operation that asks the endpoint to work the shares
    /// out again: <c>/splits</c> replaced by an explicit null.
    /// </summary>
    /// <remarks>
    /// The third of the three answers a patch can give about the division, and the only one
    /// that moves money without naming amounts. Silence keeps the stored shares -- that is
    /// what 47c6904 made it mean, after a bulk pass reading silence the other way moved
    /// 1,394.72 onto one member -- so the operation below has to be emitted on this flag
    /// and on nothing else.
    /// </remarks>
    [Fact]
    public async Task Transactions_update_redivide_asks_for_the_division_again_as_an_explicit_null()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--redivide");

        var operation = Assert.Single(Patch(id).EnumerateArray());

        Assert.Equal("replace", operation.GetProperty("op").GetString());
        Assert.Equal("/splits", operation.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Equal(JsonValueKind.Null, operation.GetProperty("value").ValueKind);
    }

    /// <summary>
    /// And beside a changed amount, which is the edit the flag exists for: the amount alone
    /// is refused because the stored shares no longer sum to it, and this is how somebody
    /// says what should happen to them instead.
    /// </summary>
    [Fact]
    public async Task Transactions_update_redivide_travels_beside_the_field_that_moved()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        await Cli.RunAsync("transactions", "update", id.ToString(), "--amount", "120", "--redivide");

        var paths = Patch(id).EnumerateArray()
            .Select(operation => operation.GetProperty("path").GetString()!.ToLowerInvariant())
            .ToList();

        Assert.Equal(["/amount", "/splits"], paths);
    }

    /// <summary>
    /// The preview is asked the same question, or it would show the shares being replaced
    /// rather than the ones about to be stored.
    /// </summary>
    /// <remarks>
    /// The preview endpoint takes a whole expense rather than the patch, so it cannot see
    /// the null operation above: a body with no shares is exactly what a save that keeps
    /// them looks like. <c>?redivide=true</c> is the one place the difference can be said,
    /// and on a changed amount it is the difference between a preview and a refusal.
    /// </remarks>
    [Fact]
    public async Task Transactions_update_redivide_asks_the_preview_for_the_division_again()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", new { id, name = "Rent", amount = 900m });

        _api.Returns($"/api/transactions/{id}/preview", new
        {
            ruleName = "Rent",
            ruleSupersededAt = (DateTimeOffset?)null,
            splits = new[] { new { userId = Guid.NewGuid(), userName = "Omar", amount = 600m } }
        });

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--amount", "1200", "--redivide",
            "--preview", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var preview = _api.Requests.Single(request => request.Path == $"/api/transactions/{id}/preview");

        Assert.Equal("true", preview.Parameter("redivide"));

        // A preview changes nothing, whatever it was asked.
        Assert.DoesNotContain(_api.Requests, request => request.Method == "PATCH");
    }

    /// <summary>
    /// Stating the shares and asking for them to be worked out are opposite instructions,
    /// so naming both is refused rather than one of them winning.
    /// </summary>
    [Fact]
    public async Task Split_and_redivide_contradict_each_other_and_nothing_is_sent()
    {
        var result = await Cli.RunAsync(
            "transactions", "update", Guid.NewGuid().ToString(),
            "--split", $"{Guid.NewGuid()}=42.50", "--redivide");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("contradict", result.Error.GetProperty("error").GetString());

        // Refused before the read the patch is built from, so an expense nobody may edit
        // cannot answer this with a different failure.
        Assert.Empty(_api.Requests);
    }

    /// <summary>
    /// On its own it is a whole edit. "Divide this the way its category says" is a change
    /// somebody means without touching any other field.
    /// </summary>
    [Fact]
    public async Task Redivide_alone_is_enough_of_an_edit_to_be_sent()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/transactions/{id}", Transaction("Dinner"));

        var result = await Cli.RunAsync("transactions", "update", id.ToString(), "--redivide");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_api.Requests, request => request.Method == "PATCH");
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
    /// Reads the balances first, so the confirmation lists the repayments it is about to
    /// write rather than describing them -- and so a client cannot decide the amounts.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_records_the_date_and_the_note()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/balances", Balance());
        _api.Returns($"/api/groups/{id}/settle-up", Settled());

        var result = await Cli.RunAsync(
            "groups", "settle-up", id.ToString(), "--date", "2026-09-30", "--note", "cash", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/settle-up").Json;

        Assert.Equal(new DateTime(2026, 9, 30), body.GetProperty("date").GetDateTimeOffset().Date);
        Assert.Equal("cash", body.GetProperty("description").GetString());
    }

    /// <summary>
    /// A dry run reads and stops. Nothing may reach the endpoint that records one.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_dry_run_writes_nothing()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/balances", Balance());
        _api.Returns($"/api/groups/{id}/settle-up", Settled());

        var result = await Cli.RunAsync("groups", "settle-up", id.ToString(), "--dry-run");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(_api.Requests, r => r.Path == $"/api/groups/{id}/settle-up");
    }

    /// <summary>
    /// Every line names the caller on one end. Squaring up is personal: money moving between
    /// two other members is not theirs to record.
    /// </summary>
    [Fact]
    public async Task Groups_settle_up_without_a_terminal_lists_both_directions()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/balances", Balance());

        var result = await Cli.RunAsync("groups", "settle-up", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);

        var changes = result.Json.GetProperty("changes").EnumerateArray()
            .Select(change => change.GetString() ?? "")
            .ToList();

        Assert.Contains("You pay Daniel 40.00.", changes);
        Assert.Contains("Omar pays you 25.00.", changes);
    }

    [Fact]
    public async Task Groups_settle_up_when_already_square_says_so_and_writes_nothing()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/balances", new
        {
            netBalances = Array.Empty<object>(),
            owedToYou = Array.Empty<object>(),
            youOwed = Array.Empty<object>()
        });

        var result = await Cli.RunAsync("groups", "settle-up", id.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(_api.Requests, r => r.Path == $"/api/groups/{id}/settle-up");
    }

    private static object Balance() => new
    {
        netBalances = Array.Empty<object>(),
        owedToYou = new[] { new { userId = Guid.NewGuid(), userName = "Omar", amount = 25.00m } },
        youOwed = new[] { new { userId = Guid.NewGuid(), userName = "Daniel", amount = 40.00m } }
    };

    private static object Settled() => new
    {
        payments = new[]
        {
            new
            {
                fromUserId = Guid.NewGuid(),
                fromUserName = "",
                toUserId = Guid.NewGuid(),
                toUserName = "Daniel",
                amount = 40.00m
            }
        },
        date = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        description = "cash"
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
    public async Task Groups_invitations_lists_the_people_a_group_is_waiting_on()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/invitations", new[] { Invitation(id, "Omar") });

        var result = await Cli.RunAsync("groups", "invitations", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Omar", result.Stdout);
    }

    /// <summary>
    /// Inviting answers with the links, and the links are the point: they are how the
    /// invitation reaches anybody at all.
    /// </summary>
    [Fact]
    public async Task Groups_invite_takes_names_and_answers_with_a_link_each()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/invitations",
            new[] { Invitation(id, "Omar"), Invitation(id, "Nuria") });

        var result = await Cli.RunAsync(
            "groups", "invite", id.ToString(), "Omar", "Nuria", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Method == "POST").Json;

        Assert.Equal("Omar", body.GetProperty("names")[0].GetString());
        Assert.Equal("Nuria", body.GetProperty("names")[1].GetString());

        Assert.Contains("Omar", result.Stdout);
        Assert.Contains("token-omar", result.Stdout);
    }

    /// <summary>
    /// Withdrawing needs confirming, and it did not used to.
    /// </summary>
    /// <remarks>
    /// The old reason it needed none was that an unanswered invitation could be sent again,
    /// so nothing was lost. That is still true of the invitation and is no longer the whole
    /// story: an invited address is somebody the group can record money against, so
    /// withdrawing can hand a fortnight of shares to a member and move their balance.
    /// </remarks>
    [Fact]
    public async Task Groups_withdraw_invitation_asks_first_because_it_can_move_money()
    {
        var id = Guid.NewGuid();
        var invitation = Guid.NewGuid();

        // Read before the gate, so the confirmation names the person rather than quoting
        // a guid -- the same order `groups remove-member` reads its roster in.
        _api.Returns($"/api/groups/{id}/invitations", new[] { Invitation(id, "Omar", invitation) });

        var result = await Cli.RunAsync(
            "groups", "withdraw-invitation", id.ToString(), invitation.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains("Omar", result.Json.GetProperty("summary").GetString()!);
        Assert.Equal("groups.withdraw-invitation", result.Json.GetProperty("action").GetString());
        Assert.DoesNotContain(_api.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task Groups_withdraw_invitation_says_whose_the_money_is_now()
    {
        var id = Guid.NewGuid();
        var invitation = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/invitations", new[] { Invitation(id, "Omar", invitation) });

        _api.Returns($"/api/groups/{id}/invitations/{invitation}", new
        {
            invitationId = invitation, groupId = id, groupName = "The flat",
            name = "Omar", outcome = (int)InvitationOutcome.Withdrawn,
            sharesMoved = 3, amountOwed = 62.50m, paymentsMoved = 1, amountPaid = 40m,
            rulesAffected = 1, absorbedByUserId = Guid.NewGuid(), absorbedByUserName = "Anabel"
        });

        var result = await Cli.RunAsync(
            "groups", "withdraw-invitation", id.ToString(), invitation.ToString(),
            "--yes", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Omar", result.Stdout);
        Assert.Contains("Anabel", result.Stdout);
        Assert.Contains(_api.Requests, r => r.Method == "DELETE");
    }

    /// <summary>
    /// The warning about a rule left naming nobody has to reach the person who caused it.
    /// </summary>
    /// <remarks>
    /// The count is the only thing in this receipt worth interrupting somebody about. A
    /// rule with nobody in it has changed what it means -- a shares or percentage rule
    /// refuses the next expense filed under it, an even one quietly divides between
    /// everybody -- and this is the last moment it can be said to whoever caused it rather
    /// than discovered by whoever records the next expense.
    /// </remarks>
    [Fact]
    public async Task Groups_withdraw_invitation_warns_about_a_rule_left_naming_nobody()
    {
        var id = Guid.NewGuid();
        var invitation = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/invitations", new[] { Invitation(id, "Omar", invitation) });

        _api.Returns($"/api/groups/{id}/invitations/{invitation}", new
        {
            invitationId = invitation, groupId = id, groupName = "The flat",
            name = "Omar", outcome = (int)InvitationOutcome.Withdrawn,
            sharesMoved = 1, amountOwed = 20m, paymentsMoved = 0, amountPaid = 0m,
            rulesAffected = 1, rulesEmptied = 1,
            absorbedByUserId = Guid.NewGuid(), absorbedByUserName = "Anabel"
        });

        var result = await Cli.RunAsync(
            "groups", "withdraw-invitation", id.ToString(), invitation.ToString(),
            "--yes", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("name nobody", result.Stdout);

        // And where to go and look, which is the point of saying it at all.
        Assert.Contains("split-rules list", result.Stdout);
    }

    /// <summary>
    /// A rule that still names somebody is not worth a warning.
    /// </summary>
    [Fact]
    public async Task Groups_withdraw_invitation_says_nothing_about_a_rule_that_still_names_somebody()
    {
        var id = Guid.NewGuid();
        var invitation = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/invitations", new[] { Invitation(id, "Omar", invitation) });

        _api.Returns($"/api/groups/{id}/invitations/{invitation}", new
        {
            invitationId = invitation, groupId = id, groupName = "The flat",
            name = "Omar", outcome = (int)InvitationOutcome.Withdrawn,
            sharesMoved = 1, amountOwed = 20m, paymentsMoved = 0, amountPaid = 0m,
            rulesAffected = 1, rulesEmptied = 0,
            absorbedByUserId = Guid.NewGuid(), absorbedByUserName = "Anabel"
        });

        var result = await Cli.RunAsync(
            "groups", "withdraw-invitation", id.ToString(), invitation.ToString(),
            "--yes", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain("name nobody", result.Stdout);
    }

    /// <summary>
    /// The members listing has to say which of them have actually joined.
    /// </summary>
    /// <remarks>
    /// It is what an agent reads to find the id to put in a split or name as the payer, and
    /// an invited address is choosable for both -- while being nobody there is an account to
    /// settle up with. Without the column the two are indistinguishable.
    /// </remarks>
    [Fact]
    public async Task Groups_members_says_who_has_joined_and_who_was_only_invited()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members", new object[]
        {
            new
            {
                id = Guid.NewGuid(), firstName = "Anabel", lastName = "Benitez",
                email = "anabel@test.com", isPendingInvitee = false
            },
            new
            {
                id = Guid.NewGuid(), firstName = (string?)null, lastName = (string?)null,
                email = "omar@test.com", isPendingInvitee = true
            }
        });

        var result = await Cli.RunAsync("groups", "members", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("joined", result.Stdout);
        Assert.Contains("invited", result.Stdout);

        // And the address stands in for the name they have not got.
        Assert.Contains("omar@test.com", result.Stdout);
    }

    /// <summary>
    /// Declining is gated for the same reason withdrawing is: it can move money.
    /// </summary>
    [Fact]
    public async Task Invitations_decline_asks_first_and_names_the_group()
    {
        _api.Returns("/api/invitations/claims/token-omar", Claim());

        var result = await Cli.RunAsync("invitations", "decline", "token-omar");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("invitations.decline", result.Json.GetProperty("action").GetString());
        Assert.Contains("The flat", result.Json.GetProperty("summary").GetString()!);
        Assert.DoesNotContain(_api.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Invitations_decline_says_whose_the_money_is_now()
    {
        _api.Returns("/api/invitations/claims/token-omar", Claim());

        _api.Returns("/api/invitations/claims/token-omar/decline", new
        {
            invitationId = Guid.NewGuid(), groupId = Guid.NewGuid(), groupName = "The flat",
            name = "Omar", outcome = (int)InvitationOutcome.Declined,
            sharesMoved = 2, amountOwed = 45m, paymentsMoved = 0, amountPaid = 0m,
            rulesAffected = 0, absorbedByUserId = Guid.NewGuid(), absorbedByUserName = "Anabel"
        });

        var result = await Cli.RunAsync(
            "invitations", "decline", "token-omar", "--yes", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("declined", result.Stdout);
        Assert.Contains("Anabel", result.Stdout);
    }

    /// <summary>
    /// Claiming is gated because it takes on a position, not because it is hard to undo.
    /// </summary>
    [Fact]
    public async Task Invitations_claim_asks_first_and_says_whose_name_it_is()
    {
        _api.Returns("/api/invitations/claims/token-omar", Claim());

        var result = await Cli.RunAsync("invitations", "claim", "token-omar");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("invitations.claim", result.Json.GetProperty("action").GetString());
        Assert.Contains("Omar", result.Json.GetProperty("summary").GetString()!);
        Assert.DoesNotContain(_api.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Invitations_claim_says_what_it_took_on()
    {
        var group = Guid.NewGuid();

        _api.Returns("/api/invitations/claims/token-omar", Claim(group));

        _api.Returns("/api/invitations/claims/token-omar", new
        {
            groupId = group, groupName = "The flat", memberCount = 3, name = "Omar",
            sharesTaken = 3, amountOwed = 62.50m, paymentsTaken = 1, amountPaid = 40m,
            rulesTaken = 2
        }, method: "POST");

        var result = await Cli.RunAsync(
            "invitations", "claim", "token-omar", "--yes", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Omar", result.Stdout);
        Assert.Contains("62.50", result.Stdout);

        // The counts above are amounts already recorded. This one is the standing
        // commitment: two rules will go on giving them a share of what is filed under
        // them, which the shares and payments cannot say.
        Assert.Contains("Rules naming you", result.Stdout);
    }

    /// <summary>
    /// The way back to a link whose message is gone.
    /// </summary>
    /// <remarks>
    /// Not "invitations sent to me", which nothing can answer without an address: the ones
    /// whose link this account has opened. Opening one is what puts it in the list, so the
    /// token is in the output -- it is what every other command here takes.
    /// </remarks>
    [Fact]
    public async Task Invitations_list_shows_the_links_you_have_opened()
    {
        _api.Returns("/api/invitations", new[] { Invitation(Guid.NewGuid(), "Omar") });

        var result = await Cli.RunAsync("invitations", "list", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Omar", result.Stdout);
        Assert.Contains("token-omar", result.Stdout);
    }

    [Fact]
    public async Task Invitations_list_is_empty_without_error_when_there_are_none()
    {
        _api.Returns("/api/invitations", Array.Empty<object>());

        var result = await Cli.RunAsync("invitations", "list", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("No open invitations", result.Stdout);
    }

    /// <summary>
    /// A whole URL is taken as readily as the token inside it: what somebody has to hand is
    /// the link they were sent.
    /// </summary>
    [Fact]
    public async Task A_pasted_url_is_cut_down_to_its_token()
    {
        _api.Returns("/api/invitations/claims/token-omar", Claim());

        var result = await Cli.RunAsync(
            "invitations", "show", "https://groupsplit.example.com/claim/token-omar",
            "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("The flat", result.Stdout);
    }

    private static object Claim(Guid? groupId = null) => new
    {
        invitationId = Guid.NewGuid(),
        groupId = groupId ?? Guid.NewGuid(),
        groupName = "The flat",
        memberCount = 2,
        name = "Omar",
        invitedByUserName = "Anabel",
        invitedAt = DateTimeOffset.UtcNow,
        alreadyAMember = false
    };

    private static object Invitation(Guid groupId, string name, Guid? id = null) => new
    {
        id = id ?? Guid.NewGuid(),
        groupId,
        groupName = "The flat",
        name,
        token = $"token-{name.ToLowerInvariant()}",
        invitedByUserName = "Anabel",
        invitedAt = DateTimeOffset.UtcNow,
        participantUserId = Guid.NewGuid()
    };

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

    /// <summary>
    /// Previewing an edit asks the endpoint made for one, and changes nothing.
    /// </summary>
    /// <remarks>
    /// Not the create preview with the edited values: an edit is divided again by the
    /// version of the rule the expense was written under, so the two endpoints answer
    /// differently and only this one answers what the save will do.
    /// </remarks>
    [Fact]
    public async Task Transactions_update_preview_asks_the_edit_endpoint_and_changes_nothing()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", new { id, name = "Rent", amount = 900m });

        _api.Returns($"/api/transactions/{id}/preview", new
        {
            ruleName = "Rent",
            ruleSupersededAt = (DateTimeOffset?)null,
            splits = new[] { new { userId = omar, userName = "Omar", amount = 400m } }
        });

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--amount", "1200", "--preview", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("Rent", result.Stdout);
        Assert.Contains("400.00", result.Stdout);

        // The whole expense, not a patch: the edited amount reaches the endpoint on a body
        // that also carries everything the flags did not touch.
        var body = _api.Requests.Single(request => request.Path == $"/api/transactions/{id}/preview").Json;

        Assert.Equal(1200m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("Rent", body.GetProperty("name").GetString());

        Assert.DoesNotContain(_api.Requests, request => request.Method == "PATCH");
    }

    /// <summary>
    /// And it previews what the save does, which is leave the shares where they are.
    /// </summary>
    /// <remarks>
    /// The preview endpoint takes a whole expense, so it cannot tell an edit that states no
    /// shares from one that asks for them again; <c>?redivide=true</c> is how the app's
    /// "Automatically" says which it means, and <c>--redivide</c> is the only thing that
    /// makes this command say it. Omitting
    /// <c>--split</c> on <c>transactions update</c> keeps the stored shares -- it has meant
    /// that since 2026-09-09 -- so a preview that showed the category's rule dividing the
    /// expense afresh would show numbers the save is not going to store, and on a changed
    /// amount it would show a success where the save is refused.
    /// </remarks>
    [Fact]
    public async Task Transactions_update_preview_does_not_ask_for_the_division_again()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", new { id, name = "Rent", amount = 900m });

        _api.Returns($"/api/transactions/{id}/preview", new
        {
            ruleName = "Rent",
            ruleSupersededAt = (DateTimeOffset?)null,
            splits = new[] { new { userId = Guid.NewGuid(), userName = "Omar", amount = 900m } }
        });

        await Cli.RunAsync("transactions", "update", id.ToString(), "--name", "Rent, March",
            "--preview", "--output", "text");

        var preview = _api.Requests.Single(request => request.Path == $"/api/transactions/{id}/preview");

        Assert.Null(preview.Parameter("redivide"));
    }

    /// <summary>
    /// And when the version that divided it is no longer current, the preview says so --
    /// which is the ordinary case on an edit and the one that otherwise reads as the
    /// server getting the rule wrong.
    /// </summary>
    [Fact]
    public async Task Transactions_update_preview_says_when_the_rule_has_changed_since()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", new { id, name = "Rent", amount = 900m });

        _api.Returns($"/api/transactions/{id}/preview", new
        {
            ruleName = "Rent",
            ruleSupersededAt = DateTimeOffset.UtcNow.AddDays(-30),
            splits = new[] { new { userId = Guid.NewGuid(), userName = "Omar", amount = 400m } }
        });

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--amount", "1200", "--preview", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("the rule has changed since", result.Stdout);
    }

    /// <summary>
    /// A settlement has one split, to the recipient, so there is no division to preview.
    /// Refused where the command already knows the kind, rather than at the API, which can
    /// only answer that no expense has that id.
    /// </summary>
    [Fact]
    public async Task Previewing_an_edit_to_a_settlement_says_there_is_nothing_to_divide()
    {
        var id = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}",
            new { id, name = "Settle up", amount = 50m, kind = (int)ActivityKind.Transfer });

        var result = await Cli.RunAsync(
            "transactions", "update", id.ToString(), "--amount", "60", "--preview");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("not divided", result.Error.GetProperty("error").GetString());
        Assert.DoesNotContain(_api.Requests, request => request.Path.EndsWith("/preview"));
    }

    /// <summary>
    /// An edit sends what moved, and nothing else -- which is what keeps <c>--name</c> from
    /// quietly re-dividing the expense.
    /// </summary>
    [Fact]
    public async Task Transactions_update_sends_only_the_fields_that_actually_moved()
    {
        var id = Guid.NewGuid();
        var group = Guid.NewGuid();

        _api.Returns($"/api/transactions/{id}", new
        {
            id, name = "Rent", amount = 900m, groupId = group, description = "March"
        });

        // The name it already has, and a new amount.
        await Cli.RunAsync("transactions", "update", id.ToString(), "--name", "Rent", "--amount", "950");

        var patch = _api.Requests.Single(request => request.Method == "PATCH").Json;

        var paths = patch.EnumerateArray()
            .Select(operation => operation.GetProperty("path").GetString()!.ToLowerInvariant())
            .ToList();

        Assert.Equal(["/amount"], paths);
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
    public async Task Split_rules_delete_says_it_will_be_refused_once_anything_was_divided_by_it()
    {
        var id = Guid.NewGuid();
        _api.Returns($"/api/split-rules/{id}", Rule("By room size", Guid.NewGuid(), id, Guid.NewGuid()));

        var result = await Cli.RunAsync("split-rules", "delete", id.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains(
            result.Json.GetProperty("changes").EnumerateArray().Select(change => change.GetString()),
            change => change!.Contains("Refused"));
    }

    /// <summary>
    /// A rule is a chain of divisions, and reading an old expense means reading the one it
    /// was divided by rather than the one the rule is on now.
    /// </summary>
    [Fact]
    public async Task Split_rules_versions_lists_every_division_the_rule_has_stood_for()
    {
        var id = Guid.NewGuid();
        var omar = Guid.NewGuid();

        _api.Returns($"/api/split-rules/{id}/versions", new
        {
            id,
            groupId = Guid.NewGuid(),
            name = "By room size",
            versions = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    startedAt = DateTimeOffset.UtcNow,
                    supersededAt = (DateTimeOffset?)null,
                    definition = new Dictionary<string, object>
                    {
                        ["$type"] = "shares",
                        ["shares"] = new Dictionary<string, int> { [omar.ToString()] = 1 }
                    }
                },
                new
                {
                    id = Guid.NewGuid(),
                    startedAt = DateTimeOffset.UtcNow.AddDays(-30),
                    supersededAt = (DateTimeOffset?)DateTimeOffset.UtcNow,
                    definition = new Dictionary<string, object>
                    {
                        ["$type"] = "shares",
                        ["shares"] = new Dictionary<string, int> { [omar.ToString()] = 3 }
                    }
                }
            }
        });

        var result = await Cli.RunAsync("split-rules", "versions", id.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Every division, and not just the one it is on. The assertion here used to be
        // `Contains("now")` alone, which the open version prints on its own -- so a listing
        // that dropped the superseded row entirely, which is the only failure this test is
        // named for, passed it.
        var divisions = LastColumnOf(result.Stdout);

        Assert.Contains("1 share", divisions, StringComparison.Ordinal);
        Assert.Contains("3 shares", divisions, StringComparison.Ordinal);

        // And they are told apart: the current one says "now" rather than a date, which is
        // the difference a reader is looking for, and exactly one version is open.
        Assert.Equal(2, result.Stdout.Split("now").Length);
    }

    /// <summary>
    /// The right-hand column of a rendered table, its cells run together.
    /// </summary>
    /// <remarks>
    /// Read cell by cell rather than by searching the output, because the table is sized to
    /// whatever width the console reports and a cell wide enough to wrap -- a guid beside a
    /// division, here -- is broken across lines at some of them and not others. Asserting on
    /// the raw text passes on the machine it was written on and fails on the next.
    /// </remarks>
    private static string LastColumnOf(string stdout) =>
        string.Join(' ', stdout
            .Split('\n')
            .Select(line => line.Split('│'))
            .Where(cells => cells.Length > 2)
            .Select(cells => cells[^2].Trim())
            .Where(cell => cell.Length > 0));

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
    /// <summary>
    /// The point of settle-between: both ends named, and the caller on neither of them.
    /// </summary>
    [Fact]
    public async Task Groups_settle_between_sends_both_ends_it_was_given()
    {
        var id = Guid.NewGuid();
        var loraine = Guid.NewGuid();
        var daniel = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members",
            new[] { Member(loraine, "Loraine"), Member(daniel, "Daniel") });

        _api.Returns($"/api/groups/{id}/repayments", new
        {
            fromUserId = loraine, fromUserName = "Loraine Haddad",
            toUserId = daniel, toUserName = "Daniel Haddad", amount = 43544.61m
        });

        var result = await Cli.RunAsync(
            "groups", "settle-between", id.ToString(), loraine.ToString(), daniel.ToString(),
            "43544.61", "--date", "2024-08-31", "--note", "Settle up August 2024", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(r => r.Path == $"/api/groups/{id}/repayments").Json;

        Assert.Equal(loraine, body.GetProperty("fromUserId").GetGuid());
        Assert.Equal(daniel, body.GetProperty("toUserId").GetGuid());
        Assert.Equal(43544.61m, body.GetProperty("amount").GetDecimal());
        Assert.Equal("Settle up August 2024", body.GetProperty("description").GetString());
        // The date is the whole reason a migration can use this: the repayment that closed
        // August 2024 belongs in August 2024, not on the day somebody typed it in.
        Assert.Equal(
            new DateTimeOffset(2024, 8, 31, 0, 0, 0, TimeSpan.Zero).Date,
            body.GetProperty("date").GetDateTimeOffset().Date);
    }

    /// <summary>
    /// It writes about two other people, so it stops for a confirmation like every other
    /// destructive change rather than going through on the strength of being asked once.
    /// </summary>
    [Fact]
    public async Task Groups_settle_between_without_yes_asks_first_and_writes_nothing()
    {
        var id = Guid.NewGuid();
        var loraine = Guid.NewGuid();
        var daniel = Guid.NewGuid();

        _api.Returns($"/api/groups/{id}/members",
            new[] { Member(loraine, "Loraine"), Member(daniel, "Daniel") });

        var result = await Cli.RunAsync(
            "groups", "settle-between", id.ToString(), loraine.ToString(), daniel.ToString(), "40.00");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.DoesNotContain(_api.Requests, r => r.Path == $"/api/groups/{id}/repayments");
    }

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
        versionId = Guid.NewGuid(), changedAt = DateTimeOffset.UtcNow,
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

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// The edits that the create routes refused and the PATCH routes let through (#315).
/// </summary>
/// <remarks>
/// Over real HTTP, because two of them only exist there: the patch document is applied in
/// the endpoint, so a malformed one never reaches a service a direct call could exercise,
/// and the settlement-to-oneself case depends on what the endpoint's model carries before
/// the patch lands on it.
/// </remarks>
public class PatchHardeningTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static StringContent PatchBody(params (string Op, string Path, object? Value)[] operations) =>
        new(JsonSerializer.Serialize(
                operations.Select(operation => new
                {
                    op = operation.Op,
                    path = operation.Path,
                    value = operation.Value
                }), Json),
            Encoding.UTF8,
            "application/json-patch+json");

    private static StringContent RawPatch(string operations) =>
        new(operations, Encoding.UTF8, "application/json-patch+json");

    private async Task<(Guid GroupId, Guid Me, Guid Other)> AGroupOfTwo()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        using var otherClient = _host.ClientForAnotherUser();
        var other = (await otherClient.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var groupResponse = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Trip" }, Json, Ct);
        groupResponse.EnsureSuccessStatusCode();
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        var invited = await Client.PostAsJsonAsync($"/groups/{groupId}/invitations",
            new InviteToGroupRequest { Names = ["Other"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();

        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;

        var accepted = await otherClient.PostAsync($"/invitations/claims/{pending[0].Token}", null, Ct);
        accepted.EnsureSuccessStatusCode();

        return (groupId, me.Id, other.Id);
    }

    /// <summary>The other member paying me 30, and the settlement that records it.</summary>
    private async Task<(Guid GroupId, Guid Me, Guid Other, Guid SettlementId)> ARepayment()
    {
        var (groupId, me, other) = await AGroupOfTwo();

        var settled = await Client.PostAsJsonAsync($"/groups/{groupId}/settle", new SettleRequest
        {
            UserId = other,
            Amount = 30m,
            Direction = SettlementDirection.TheyPaidYou
        }, Json, Ct);
        settled.EnsureSuccessStatusCode();

        var history = await Client.GetFromJsonAsync<JsonElement>("/users/me/settlements", Json, Ct);
        var settlementId = history.GetProperty("items")[0].GetProperty("id").GetGuid();

        return (groupId, me, other, settlementId);
    }

    private async Task<Dictionary<Guid, decimal>> Balances(Guid groupId)
    {
        var balances = await Client.GetFromJsonAsync<UserGroupBalanceResponse>(
            $"/groups/{groupId}/balances", Json, Ct);

        return balances!.NetBalances.ToDictionary(balance => balance.UserId, balance => balance.Balance);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("code").GetString();

    // ---- Settlements -------------------------------------------------------------------

    /// <summary>
    /// A repayment of 30 patched to -30 was stored, and moved the balances by 60 the wrong
    /// way: the "repayment" added to what the payer owed.
    /// </summary>
    [Theory]
    [InlineData(-30, ErrorCodes.SettlementAmountNotPositive)]
    [InlineData(0, ErrorCodes.ValidationFailed)]
    public async Task A_settlement_patched_to_zero_or_less_is_refused_and_moves_nothing(
        decimal amount, string code)
    {
        var (groupId, _, _, settlementId) = await ARepayment();
        var before = await Balances(groupId);

        var response = await Client.PatchAsync($"/transactions/{settlementId}",
            PatchBody(("replace", "/amount", amount)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeOf(response));
        Assert.Equal(before, await Balances(groupId));
    }

    /// <summary>
    /// Removing the shares took the branch that never asked who the money went to, so
    /// moving the payer onto the recipient wrote a payment from somebody to themselves and
    /// took the original payer's credit away.
    /// </summary>
    [Fact]
    public async Task Dropping_the_shares_and_moving_the_payer_onto_the_recipient_is_refused()
    {
        var (_, me, _, settlementId) = await ARepayment();

        var response = await Client.PatchAsync($"/transactions/{settlementId}",
            PatchBody(("remove", "/splits", null), ("replace", "/paidByUserId", me)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.SettlementWithSelf, await CodeOf(response));
    }

    [Fact]
    public async Task Moving_the_payer_onto_the_recipient_is_refused_with_the_shares_left_alone()
    {
        var (_, me, _, settlementId) = await ARepayment();

        var response = await Client.PatchAsync($"/transactions/{settlementId}",
            PatchBody(("replace", "/paidByUserId", me)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.SettlementWithSelf, await CodeOf(response));
    }

    /// <summary>Two recipients used to be read as the first one, silently.</summary>
    [Fact]
    public async Task A_settlement_paid_to_two_people_is_refused()
    {
        var (_, me, other, settlementId) = await ARepayment();

        var response = await Client.PatchAsync($"/transactions/{settlementId}",
            PatchBody(("replace", "/splits", new[]
            {
                new SplitInput { UserId = me, Amount = 15m },
                new SplitInput { UserId = other, Amount = 15m }
            })), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.SplitsInvalid, await CodeOf(response));
    }

    [Fact]
    public async Task A_settlement_can_still_be_corrected()
    {
        var (groupId, me, other, settlementId) = await ARepayment();

        var response = await Client.PatchAsync($"/transactions/{settlementId}",
            PatchBody(("replace", "/amount", 25m), ("replace", "/description", "cash")), Ct);

        response.EnsureSuccessStatusCode();
        var balances = await Balances(groupId);
        Assert.Equal(-25m, balances[me]);
        Assert.Equal(25m, balances[other]);
    }

    // ---- Expenses ----------------------------------------------------------------------

    /// <summary>
    /// A rule-divided expense patched to zero re-divided its shares to zeros and stored
    /// them. Create has refused zero all along.
    /// </summary>
    [Fact]
    public async Task An_expense_patched_to_zero_is_refused()
    {
        var (groupId, me, _) = await AGroupOfTwo();
        var expense = await AnExpense(groupId, me, 100m);

        var response = await Client.PatchAsync($"/transactions/{expense}",
            PatchBody(("replace", "/amount", 0m)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(response));
    }

    /// <summary>
    /// {A: 150, B: -100} sums to 50 and was accepted: a repayment of 100 folded into a row
    /// that says it is an expense.
    /// </summary>
    [Fact]
    public async Task A_share_going_against_the_amount_is_refused()
    {
        var (groupId, me, other) = await AGroupOfTwo();

        var response = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 50m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = me,
            Splits =
            [
                new SplitInput { UserId = me, Amount = 150m },
                new SplitInput { UserId = other, Amount = -100m }
            ]
        }, Json, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.SplitsInvalid, await CodeOf(response));
    }

    [Fact]
    public async Task A_refund_still_divides_into_shares_that_are_all_money_back()
    {
        var (groupId, me, other) = await AGroupOfTwo();

        var response = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Refund",
            Amount = -50m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = me,
            Splits =
            [
                new SplitInput { UserId = me, Amount = -30m },
                new SplitInput { UserId = other, Amount = -20m }
            ]
        }, Json, Ct);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// <c>[Required]</c> on a non-nullable date never fails, so an expense with no date was
    /// stored on 0001-01-01.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_date_is_refused()
    {
        using var body = new StringContent("{\"name\":\"Lunch\",\"amount\":10}", Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/transactions", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(response));
    }

    // ---- Malformed patches -------------------------------------------------------------

    /// <summary>
    /// Every one of these threw <c>JsonPatchException</c> out of the endpoint, unmapped,
    /// and answered 500.
    /// </summary>
    [Theory]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/nothingHere\",\"value\":1}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/amount\",\"value\":\"abc\"}]")]
    [InlineData("[{\"op\":\"test\",\"path\":\"/name\",\"value\":\"Not the name\"}]")]
    public async Task A_transaction_patch_that_cannot_apply_is_refused_as_malformed(string operations)
    {
        var (groupId, me, _) = await AGroupOfTwo();
        var expense = await AnExpense(groupId, me, 100m);

        var response = await Client.PatchAsync($"/transactions/{expense}", RawPatch(operations), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.PatchInvalid, await CodeOf(response));
    }

    [Theory]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/nothingHere\",\"value\":1}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/defaultSplitRuleId\",\"value\":\"abc\"}]")]
    [InlineData("[{\"op\":\"replace\",\"path\":\"/defaultPeriod\",\"value\":\"Fortnight\"}]")]
    [InlineData("[{\"op\":\"test\",\"path\":\"/name\",\"value\":\"Not the name\"}]")]
    public async Task A_group_patch_that_cannot_apply_is_refused_as_malformed(string operations)
    {
        var (groupId, _, _) = await AGroupOfTwo();

        var response = await Client.PatchAsync($"/groups/{groupId}", RawPatch(operations), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.PatchInvalid, await CodeOf(response));
    }

    /// <summary>
    /// Null in the model is how the endpoint hears "the patch said nothing about the
    /// default", so a patch asking for no default was read as silence and ignored. Every
    /// group divides by one, so asking is refused out loud instead.
    /// </summary>
    [Fact]
    public async Task Clearing_a_groups_default_rule_is_refused_rather_than_ignored()
    {
        var (groupId, _, _) = await AGroupOfTwo();

        var response = await Client.PatchAsync($"/groups/{groupId}",
            PatchBody(("replace", "/defaultSplitRuleId", null)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.SplitRuleInvalid, await CodeOf(response));
    }

    [Fact]
    public async Task Renaming_a_group_still_keeps_its_default_rule()
    {
        var (groupId, _, _) = await AGroupOfTwo();
        var before = await Client.GetFromJsonAsync<GroupResponse>($"/groups/{groupId}", Json, Ct);

        var response = await Client.PatchAsync($"/groups/{groupId}",
            PatchBody(("replace", "/name", "Renamed")), Ct);

        response.EnsureSuccessStatusCode();
        var after = await Client.GetFromJsonAsync<GroupResponse>($"/groups/{groupId}", Json, Ct);
        Assert.Equal(before!.DefaultSplitRuleId, after!.DefaultSplitRuleId);
    }

    /// <summary>
    /// The span the Activity tab opens on (#405): This month until a member picks another,
    /// and then that one, through a rename that says nothing about it.
    /// </summary>
    [Fact]
    public async Task A_groups_default_period_is_set_by_the_patch_and_kept_by_a_rename()
    {
        var (groupId, _, _) = await AGroupOfTwo();
        var before = await Client.GetFromJsonAsync<GroupResponse>($"/groups/{groupId}", Json, Ct);

        Assert.Equal(ActivityPeriod.ThisMonth, before!.DefaultPeriod);

        (await Client.PatchAsync($"/groups/{groupId}",
            PatchBody(("replace", "/defaultPeriod", "AllTime")), Ct)).EnsureSuccessStatusCode();
        (await Client.PatchAsync($"/groups/{groupId}",
            PatchBody(("replace", "/name", "Renamed")), Ct)).EnsureSuccessStatusCode();

        var after = await Client.GetFromJsonAsync<GroupResponse>($"/groups/{groupId}", Json, Ct);
        Assert.Equal(ActivityPeriod.AllTime, after!.DefaultPeriod);
    }

    /// <summary>A number reads as an enum value; one that names no period is still refused.</summary>
    [Fact]
    public async Task A_default_period_that_names_no_period_is_refused()
    {
        var (groupId, _, _) = await AGroupOfTwo();

        var response = await Client.PatchAsync($"/groups/{groupId}",
            PatchBody(("replace", "/defaultPeriod", 99)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Guid> AnExpense(Guid groupId, Guid payerId, decimal amount)
    {
        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = payerId
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        return (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!.Id;
    }
}

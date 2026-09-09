using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Editing an expense's division over the wire, which is the one place keeping
/// <c>PATCH</c> costs something.
/// </summary>
/// <remarks>
/// A patch can change part of a model, and the shares are the part that has to stay
/// consistent with the rest of it: a patch that changes the amount and says nothing about
/// the shares would otherwise leave shares that no longer sum to it, and every balance in
/// the group is that sum.
/// <para>
/// So silence about the shares is an instruction, not an omission -- it means "divide it
/// again" -- and the endpoint can only tell silence from a statement by reading the
/// operations before applying them. These go over real HTTP because that reading is the
/// behaviour under test, and calling the service directly skips the patch document
/// entirely.
/// </para>
/// </remarks>
public class TransactionSplitPatchTest : IAsyncLifetime
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

    /// <summary>
    /// A group of two with an expense of 100 split evenly, which is the shape every test
    /// below edits.
    /// </summary>
    private async Task<(Guid TransactionId, Guid Me, Guid Other)> AnEvenlySplitExpense()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        using var otherClient = _host.ClientForAnotherUser();
        var other = (await otherClient.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var groupResponse = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Trip" }, Json, Ct);
        groupResponse.EnsureSuccessStatusCode();
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct))!.Id;

        // Two calls, because joining is now something the invitee agrees to: the group asks,
        // and they accept. There is no route left that puts somebody in a group without it.
        var invited = await Client.PostAsJsonAsync($"/groups/{groupId}/invitations",
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]), Json, Ct);
        invited.EnsureSuccessStatusCode();

        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;

        var accepted = await otherClient.PostAsync($"/invitations/{pending[0].Id}/accept", null, Ct);
        accepted.EnsureSuccessStatusCode();

        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = me.Id
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var transaction = (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;

        return (transaction.Id, me.Id, other.Id);
    }

    private async Task<TransactionDetailsResponse> Details(Guid transactionId) =>
        (await Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{transactionId}", Json, Ct))!;

    private static decimal ShareOf(TransactionDetailsResponse details, Guid userId) =>
        details.Splits.Single(split => split.UserId == userId).Amount;

    private async Task<ProblemDetails> Refused(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);

        var problem = JsonSerializer.Deserialize<ProblemDetails>(
            await response.Content.ReadAsStringAsync(Ct), Json);

        Assert.NotNull(problem);
        return problem;
    }

    /// <summary>
    /// The shares travel with their user ids, because the edit dialog sends them back and
    /// a name is not something the API can be addressed by.
    /// </summary>
    [Fact]
    public async Task The_details_of_an_expense_name_who_owes_each_share()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var details = await Details(transactionId);

        Assert.Equal(2, details.Splits.Count);
        Assert.Equal(50m, ShareOf(details, me));
        Assert.Equal(50m, ShareOf(details, other));
        Assert.All(details.Splits, split => Assert.NotEqual(Guid.Empty, split.UserId));
    }

    /// <summary>
    /// Patching the amount alone without updating the shares is refused because the
    /// existing shares no longer sum to the new amount.
    /// </summary>
    [Fact]
    public async Task Patching_the_amount_alone_without_updating_splits_is_refused()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/amount", 250m)), Ct);

        var problem = await Refused(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, problem.Code);
    }

    /// <summary>
    /// Patching the payer updates who paid while preserving the division.
    /// </summary>
    [Fact]
    public async Task Patching_the_payer_alone_preserves_the_shares()
    {
        var (transactionId, _, other) = await AnEvenlySplitExpense();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/paidByUserId", other)), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(other, details.PaidByUserId);
        Assert.Equal(100m, details.Splits.Sum(split => split.Amount));
    }

    [Fact]
    public async Task Patching_the_shares_keeps_exactly_what_was_sent()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 90m },
                new { userId = other, amount = 10m }
            })), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(90m, ShareOf(details, me));
        Assert.Equal(10m, ShareOf(details, other));
    }

    /// <summary>
    /// Both at once, which is the ordinary way somebody changes what an expense cost and
    /// who carries it: the shares are checked against the amount the same patch set, not
    /// the one it replaced.
    /// </summary>
    [Fact]
    public async Task Patching_the_amount_and_the_shares_together_checks_them_against_each_other()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/amount", 60m),
                ("replace", "/splits", new[]
                {
                    new { userId = me, amount = 20m },
                    new { userId = other, amount = 40m }
                })), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(60m, details.Amount);
        Assert.Equal(20m, ShareOf(details, me));
        Assert.Equal(40m, ShareOf(details, other));
    }

    /// <summary>
    /// Shares stated against an amount they do not add up to are refused rather than
    /// adjusted, and the refusal says by how much.
    /// </summary>
    [Fact]
    public async Task Shares_that_do_not_add_up_to_the_patched_amount_are_refused()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/amount", 60m),
                ("replace", "/splits", new[]
                {
                    new { userId = me, amount = 20m },
                    new { userId = other, amount = 20m }
                })), Ct);

        var problem = await Refused(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, problem.Code);

        // And nothing moved: the expense is as it was.
        var details = await Details(transactionId);
        Assert.Equal(100m, details.Amount);
        Assert.Equal(50m, ShareOf(details, me));
    }

    /// <summary>
    /// One share addressed by index is a statement about the division too -- the rest of
    /// the list has to stay put for that edit to mean anything, so it is not recomputed.
    /// </summary>
    [Fact]
    public async Task Patching_a_single_share_by_index_is_taken_as_stating_them_all()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var before = await Details(transactionId);
        var firstIsMine = before.Splits[0].UserId == me;

        // Move ten from the first share to the second, so the pair still sums to 100.
        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(
                ("replace", "/splits/0/amount", 40m),
                ("replace", "/splits/1/amount", 60m)), Ct);
        response.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal(100m, details.Splits.Sum(split => split.Amount));
        Assert.Equal(firstIsMine ? 40m : 60m, ShareOf(details, me));
        Assert.Equal(firstIsMine ? 60m : 40m, ShareOf(details, other));
    }

    /// <summary>
    /// The same edit left half-done: one share changed and the other not, so the pair no
    /// longer sums. Refused, because the alternative is a group whose balances quietly
    /// stop adding up to zero.
    /// </summary>
    [Fact]
    public async Task Patching_one_share_and_leaving_the_rest_short_is_refused()
    {
        var (transactionId, _, _) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits/0/amount", 40m)), Ct);

        var problem = await Refused(response, HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, problem.Code);
    }

    /// <summary>
    /// A patch of something unrelated leaves existing custom shares intact without
    /// resetting/re-splitting by category.
    /// </summary>
    [Fact]
    public async Task Patching_only_the_name_leaves_the_shares_alone()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        // Stating custom splits (70 / 30)
        var customSplitsResponse = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 70m },
                new { userId = other, amount = 30m }
            })), Ct);
        customSplitsResponse.EnsureSuccessStatusCode();

        var patched = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/name", "Hotel, two nights")), Ct);
        patched.EnsureSuccessStatusCode();

        var details = await Details(transactionId);

        Assert.Equal("Hotel, two nights", details.Name);
        Assert.Equal(70m, ShareOf(details, me));
        Assert.Equal(30m, ShareOf(details, other));
    }

    [Fact]
    public async Task A_share_for_somebody_outside_the_group_is_refused()
    {
        var (transactionId, me, _) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 50m },
                new { userId = Guid.NewGuid(), amount = 50m }
            })), Ct);

        var problem = await Refused(response, HttpStatusCode.Conflict);

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, problem.Code);
    }

    /// <summary>
    /// An expense can be created with its division stated outright, which is what the
    /// dialog sends when somebody sets the shares themselves.
    /// </summary>
    [Fact]
    public async Task An_expense_can_be_created_with_its_shares_stated()
    {
        var (_, me, other) = await AnEvenlySplitExpense();

        var groups = await Client.GetFromJsonAsync<List<GroupResponse>>("/groups", Json, Ct);
        var groupId = groups!.Single(group => group.Name == "Trip").Id;

        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Cake",
            Amount = 30m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = me,
            Splits = [new SplitInput { UserId = me, Amount = 30m }]
        }, Json, Ct);
        created.EnsureSuccessStatusCode();

        var transaction = (await created.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct))!;
        var details = await Details(transaction.Id);

        var share = Assert.Single(details.Splits);
        Assert.Equal(me, share.UserId);
        Assert.Equal(30m, share.Amount);
        Assert.DoesNotContain(details.Splits, split => split.UserId == other);
    }

    /// <summary>
    /// A refusal is problem details like every other, so a client reads one shape whatever
    /// went wrong -- and the shortfall rides along for a dialog to show.
    /// </summary>
    [Fact]
    public async Task The_refusal_is_problem_details_carrying_the_shortfall()
    {
        var (transactionId, me, other) = await AnEvenlySplitExpense();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/splits", new[]
            {
                new { userId = me, amount = 30m },
                new { userId = other, amount = 20m }
            })), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(50m, body.RootElement.GetProperty("splitTotal").GetDecimal());
        Assert.Equal(100m, body.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(50m, body.RootElement.GetProperty("difference").GetDecimal());
    }
}

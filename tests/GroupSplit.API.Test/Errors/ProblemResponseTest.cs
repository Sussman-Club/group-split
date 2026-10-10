using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.Extensions.Logging;

namespace GroupSplit.API.Test.Errors;

/// <summary>
/// The error contract, checked where a client meets it: every non-2xx answer is
/// <c>application/problem+json</c> carrying a stable <c>code</c> and a <c>traceId</c>, and
/// each category of failure -- not found, forbidden, conflict, validation, unauthenticated,
/// and a bug -- lands on its own status with its own code. <c>docs/errors.md</c> describes
/// the shape these pin.
/// </summary>
public class ProblemResponseTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // ---- Helpers ----------------------------------------------------------------------

    /// <summary>
    /// The invariant part of the contract, asserted for every problem read in this file.
    /// </summary>
    private static async Task<ProblemDetails> ReadProblem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(Ct);
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, Json);

        Assert.NotNull(problem);
        Assert.Equal((int)expected, problem.Status);
        Assert.False(string.IsNullOrEmpty(problem.Code), $"every problem carries a code, this one was: {body}");
        Assert.False(string.IsNullOrEmpty(problem.TraceId), $"every problem carries a trace id, this one was: {body}");
        Assert.Equal(Problems.TypeFor(problem.Code), problem.Type);
        Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, problem.Instance);

        return problem;
    }

    private async Task<UserInfo> WhoAmI(HttpClient client) =>
        (await client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

    private async Task<Guid> CreateGroup(string name = "Trip")
    {
        var response = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = name }, Json, Ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<GroupResponse>(Json, Ct);
        return created!.Id;
    }

    private async Task<Guid> CreateTransaction(decimal amount = 10m, Guid? categoryId = null, Guid? paidBy = null,
        Guid? groupId = null)
    {
        var response = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            PaidByUserId = paidBy
        }, Json, Ct);

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<TransactionResponse>(Json, Ct);
        return created!.Id;
    }

    private async Task<HttpResponseMessage> CreateSplitRule(Guid groupId, string name, SplitRuleDto definition) =>
        await Client.PostAsJsonAsync("/split-rules", new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = name,
            Definition = definition
        }, Json, Ct);

    private async Task<HttpResponseMessage> CreateCategory(Guid groupId, string name, Guid? splitRuleId = null) =>
        await Client.PostAsJsonAsync("/categories", new CreateCategoryRequest
        {
            GroupId = groupId,
            Name = name,
            DefaultSplitRuleId = splitRuleId
        }, Json, Ct);

    /// <summary>
    /// A group of two where the other member owes the host's user half of a bill: the
    /// state that blocks both removing that member and deleting the account.
    /// </summary>
    private async Task<(Guid GroupId, UserInfo Me, UserInfo Other)> GroupWhereTheOtherOwesMe()
    {
        var me = await WhoAmI(Client);
        using var otherClient = _host.ClientForAnotherUser();
        var other = await WhoAmI(otherClient);

        var groupId = await CreateGroup("Ski trip");

        // Two calls, because joining is now something the invitee agrees to: the group asks,
        // and they accept. There is no route left that puts somebody in a group without it.
        var invited = await Client.PostAsJsonAsync($"/groups/{groupId}/invitations",
            new InviteToGroupRequest { Names = ["Other"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();

        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;

        var accepted = await otherClient.PostAsync($"/invitations/claims/{pending[0].Token}", null, Ct);
        accepted.EnsureSuccessStatusCode();

        var rule = await CreateSplitRule(groupId, "Lift passes", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [me.Id] = 50m, [other.Id] = 50m }
        });
        rule.EnsureSuccessStatusCode();
        var splitRule = await rule.Content.ReadFromJsonAsync<SplitRuleDetailsResponse>(Json, Ct);

        var category = await CreateCategory(groupId, "Lift passes", splitRule!.Id);
        category.EnsureSuccessStatusCode();
        var created = await category.Content.ReadFromJsonAsync<CategoryResponse>(Json, Ct);

        await CreateTransaction(100m, created!.Id, me.Id, groupId);

        return (groupId, me, other);
    }

    // ---- Not found ----------------------------------------------------------------------

    [Fact]
    public async Task A_transaction_that_does_not_exist_is_a_problem_with_its_own_code()
    {
        var response = await Client.GetAsync($"/transactions/{Guid.NewGuid()}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.NotFound);

        Assert.Equal(ErrorCodes.TransactionNotFound, problem.Code);
        Assert.Equal("Transaction not found", problem.Title);
    }

    /// <summary>
    /// The refusal here is thrown by the service rather than returned by the endpoint, and
    /// until the exception handler existed it escaped as a 500.
    /// </summary>
    [Fact]
    public async Task A_refusal_thrown_by_a_service_is_a_problem_not_a_500()
    {
        var transactionId = await CreateTransaction();
        using var stranger = _host.ClientForAnotherUser();

        var response = await stranger.DeleteAsync($"/transactions/{transactionId}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(ErrorCodes.TransactionNotFound, problem.Code);
    }

    [Fact]
    public async Task A_group_the_caller_is_not_in_has_no_balances_to_show()
    {
        var response = await Client.GetAsync($"/groups/{Guid.NewGuid()}/balances", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(ErrorCodes.GroupNotFound, problem.Code);
    }

    // ---- Forbidden ----------------------------------------------------------------------

    [Fact]
    public async Task Removing_yourself_from_a_group_is_forbidden()
    {
        var me = await WhoAmI(Client);
        var groupId = await CreateGroup();

        var response = await Client.DeleteAsync($"/groups/{groupId}/members/{me.Id}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Forbidden);
        Assert.Equal(ErrorCodes.GroupCannotRemoveSelf, problem.Code);
    }

    // ---- Conflict -----------------------------------------------------------------------

    [Fact]
    public async Task A_second_category_of_the_same_name_is_a_conflict()
    {
        var groupId = await CreateGroup();
        (await CreateCategory(groupId, "Groceries")).EnsureSuccessStatusCode();

        var response = await CreateCategory(groupId, "Groceries");

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.CategoryNameTaken, problem.Code);
    }

    [Fact]
    public async Task Removing_a_member_who_has_not_settled_is_a_conflict_that_names_the_balance()
    {
        var (groupId, _, other) = await GroupWhereTheOtherOwesMe();

        var response = await Client.DeleteAsync($"/groups/{groupId}/members/{other.Id}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.GroupMemberNotSettled, problem.Code);
        Assert.Equal(-50m, problem.GetExtension<decimal>("balance", Json));
    }

    /// <summary>
    /// Deleting an expense a removed member shared would leave them owed money nobody on the
    /// page owes back: refused, naming who.
    /// </summary>
    [Fact]
    public async Task Deleting_an_expense_a_departed_member_shared_is_a_conflict_that_names_them()
    {
        var (groupId, _, other) = await GroupWhereTheOtherOwesMe();

        (await Client.PostAsJsonAsync($"/groups/{groupId}/settle",
            new SettleRequest { UserId = other.Id, Amount = 50m }, Json, Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/groups/{groupId}/members/{other.Id}", Ct)).EnsureSuccessStatusCode();

        var expense = (await Client.GetFromJsonAsync<PagedResponse<TransactionResponse>>(
            $"/groups/{groupId}/transactions", Json, Ct))!.Items.First();

        var response = await Client.DeleteAsync($"/transactions/{expense.Id}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.TransactionMovesDepartedBalance, problem.Code);
        Assert.Equal([other.Id], problem.GetExtension<List<Guid>>("userIds", Json));
    }

    /// <summary>
    /// Somebody removed from a group opening an invitation they saw while they were still in
    /// it: refused on the landing page, with what the page needs to say why.
    /// </summary>
    [Fact]
    public async Task An_invitation_older_than_a_removal_is_a_conflict_that_says_so()
    {
        using var otherClient = _host.ClientForAnotherUser();
        var other = await WhoAmI(otherClient);

        var groupId = await CreateGroup("Flat");

        var invited = await Client.PostAsJsonAsync($"/groups/{groupId}/invitations",
            new InviteToGroupRequest { Names = ["Other", "Dani"] }, Json, Ct);
        invited.EnsureSuccessStatusCode();

        var pending = (await invited.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct))!;
        var theirs = pending.Single(invitation => invitation.Name == "Other");
        var danis = pending.Single(invitation => invitation.Name == "Dani");

        (await otherClient.PostAsync($"/invitations/claims/{theirs.Token}", null, Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/groups/{groupId}/members/{other.Id}", Ct)).EnsureSuccessStatusCode();

        var response = await otherClient.GetAsync($"/invitations/claims/{danis.Token}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.GroupRejoinNeedsNewInvitation, problem.Code);
        Assert.Equal("Removed", problem.GetExtension<string>("leftReason", Json));
        Assert.NotEqual(default, problem.GetExtension<DateTimeOffset>("leftAt", Json));
        Assert.Equal(danis.InvitedAt, problem.GetExtension<DateTimeOffset>("issuedAt", Json));
    }

    /// <summary>
    /// The one refusal the app already reacted to, moved onto the contract: the groups that
    /// block the deletion ride on the problem as an extension member, so the client branches
    /// on the code and reads the list, rather than on the shape of the body.
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_with_outstanding_balances_is_a_conflict_naming_the_groups()
    {
        var (groupId, _, _) = await GroupWhereTheOtherOwesMe();

        var response = await Client.DeleteAsync("/users/me", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.AccountNotSettled, problem.Code);

        var blocking = problem.GetExtension<List<OutstandingBalance>>(
            ProblemDetails.OutstandingBalancesExtension, Json);

        var only = Assert.Single(blocking!);
        Assert.Equal(groupId, only.GroupId);
        Assert.Equal("Ski trip", only.GroupName);
        Assert.Equal(50m, only.Balance);

        // Refused means untouched.
        var me = await Client.GetAsync("/users/me", Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    /// <summary>
    /// Somebody deleted and recreated in Keycloak signs in with a new subject and the
    /// address the old row still holds. That used to be a 500 on every request, from the
    /// middleware, before any endpoint ran. It is a refusal with a code support can act on,
    /// and it stays one: the same answer on the next request, not a different failure.
    /// </summary>
    [Fact]
    public async Task A_new_sign_in_with_an_email_another_account_holds_is_a_conflict_not_a_500()
    {
        var existing = await WhoAmI(Client);
        using var recreated = _host.ClientFor(Guid.NewGuid().ToString(), existing.Email!);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var response = await recreated.GetAsync("/users/me", Ct);

            var problem = await ReadProblem(response, HttpStatusCode.Conflict);
            Assert.Equal(ErrorCodes.IdentityEmailInUse, problem.Code);
        }

        // The account that holds the address is untouched by it.
        Assert.Equal(existing.Id, (await WhoAmI(Client)).Id);
    }

    // ---- Validation ---------------------------------------------------------------------

    /// <summary>A rule the annotations cannot express, thrown from the service.</summary>
    [Fact]
    public async Task Percentages_that_do_not_add_up_are_a_validation_problem()
    {
        var me = await WhoAmI(Client);
        var groupId = await CreateGroup();

        var response = await CreateSplitRule(groupId, "Lopsided", new PercentSplitRuleDto
        {
            Percentages = new Dictionary<Guid, decimal> { [me.Id] = 50m }
        });

        var problem = await ReadProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.SplitRuleInvalid, problem.Code);
    }

    /// <summary>
    /// A failure the annotations do express, reported by the framework's own validation
    /// problem, which the customization still stamps with a code and a trace id.
    /// </summary>
    [Fact]
    public async Task A_patch_that_fails_validation_is_a_validation_problem_with_the_fields()
    {
        var transactionId = await CreateTransaction();

        var patch = new StringContent(
            """[{"op":"replace","path":"/amount","value":10.005}]""",
            Encoding.UTF8,
            "application/json-patch+json");

        var response = await Client.PatchAsync($"/transactions/{transactionId}", patch, Ct);

        var problem = await ReadProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ValidationFailed, problem.Code);

        var errors = problem.GetExtension<Dictionary<string, string[]>>(ProblemDetails.ErrorsExtension, Json);
        Assert.NotNull(errors);
        Assert.Contains("Amount", errors.Keys);
    }

    /// <summary>
    /// A patch that cannot be applied at all is a different failure from one that applies
    /// and leaves an invalid model, and has its own code. It used to be an unmapped
    /// exception, so a 500.
    /// </summary>
    [Fact]
    public async Task A_patch_that_cannot_be_applied_is_a_problem_with_its_own_code()
    {
        var transactionId = await CreateTransaction();

        var patch = new StringContent(
            """[{"op":"replace","path":"/amount","value":"abc"}]""",
            Encoding.UTF8,
            "application/json-patch+json");

        var response = await Client.PatchAsync($"/transactions/{transactionId}", patch, Ct);

        var problem = await ReadProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.PatchInvalid, problem.Code);
        Assert.False(string.IsNullOrEmpty(problem.Detail));
    }

    // ---- Produced by the framework, not the domain -----------------------------------------

    [Fact]
    public async Task An_anonymous_request_is_a_problem_too()
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.GetAsync("/groups", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Unauthorized);
        Assert.Equal(ErrorCodes.Unauthenticated, problem.Code);
    }

    [Fact]
    public async Task A_route_that_does_not_exist_is_a_problem_too()
    {
        var response = await Client.GetAsync("/nothing/here", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(ErrorCodes.NotFound, problem.Code);
    }

    // ---- Bugs ---------------------------------------------------------------------------

    [Fact]
    public async Task An_unhandled_exception_is_a_500_that_leaks_nothing_and_is_logged_with_the_trace_id()
    {
        var response = await Client.GetAsync(ApiEndpointHost.ThrowingRoute, Ct);

        var problem = await ReadProblem(response, HttpStatusCode.InternalServerError);
        Assert.Equal(ErrorCodes.InternalError, problem.Code);
        Assert.Equal(UnhandledExceptionHandler.Detail, problem.Detail);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);

        var logged = Assert.Single(_host.Logs, log => log.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(logged.Exception);
        Assert.Contains(problem.TraceId!, logged.Message, StringComparison.Ordinal);
    }

    // ---- Races the database settles -------------------------------------------------------

    /// <summary>
    /// Two saves of one row at once: the concurrency token refuses the second, and that is a
    /// documented conflict rather than a bug.
    /// </summary>
    [Fact]
    public async Task A_save_that_lost_to_another_is_a_conflict_not_a_500()
    {
        var response = await Client.GetAsync(ApiEndpointHost.LostRaceRoute, Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.ConcurrentChange, problem.Code);
        Assert.DoesNotContain(_host.Logs, log => log.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// The unique indexes behind a check the services already make answer with that
    /// check's code: two requests can both pass it, and the index is what stops the second.
    /// </summary>
    [Theory]
    [InlineData("IX_Category_GroupId_Name", ErrorCodes.CategoryNameTaken)]
    [InlineData("IX_SplitRule_GroupId_Name", ErrorCodes.SplitRuleNameTaken)]
    [InlineData("IX_Transaction_BankTransactionId", ErrorCodes.BankTransactionAlreadyFiled)]
    [InlineData("IX_Receipt_ExpenseId", ErrorCodes.ConcurrentChange)]
    public async Task A_unique_index_settling_a_race_answers_with_its_documented_code(string index, string code)
    {
        var response = await Client.GetAsync($"{ApiEndpointHost.LostRaceRoute}?index={index}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(code, problem.Code);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(index, body, StringComparison.Ordinal);
    }

    /// <summary>An index nothing expects to be raced is a write nobody meant: still a bug.</summary>
    [Fact]
    public async Task A_unique_violation_on_any_other_index_is_still_a_500()
    {
        var response = await Client.GetAsync($"{ApiEndpointHost.LostRaceRoute}?index=IX_User_Email", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.InternalServerError);
        Assert.Equal(ErrorCodes.InternalError, problem.Code);
    }

    /// <summary>Expected refusals, by contrast, are not errors in the log.</summary>
    [Fact]
    public async Task A_refusal_is_logged_as_information_not_error()
    {
        var me = await WhoAmI(Client);
        var groupId = await CreateGroup();

        await Client.DeleteAsync($"/groups/{groupId}/members/{me.Id}", Ct);

        Assert.DoesNotContain(_host.Logs, log => log.Level >= LogLevel.Warning);
        Assert.Contains(_host.Logs, log =>
            log.Level == LogLevel.Information
            && log.Message.Contains(ErrorCodes.GroupCannotRemoveSelf, StringComparison.Ordinal));
    }

    // ---- Content negotiation ----------------------------------------------------------------

    /// <summary>
    /// The problem details service declines to write for a client that did not ask for
    /// JSON. The contract does not: a client that cannot read problem+json is still owed
    /// something better than an empty body.
    /// </summary>
    [Fact]
    public async Task A_client_that_asks_for_text_still_gets_problem_json()
    {
        var transactionId = await CreateTransaction();
        using var stranger = _host.ClientForAnotherUser();
        stranger.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        var response = await stranger.DeleteAsync($"/transactions/{transactionId}", Ct);

        var problem = await ReadProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(ErrorCodes.TransactionNotFound, problem.Code);
    }
}

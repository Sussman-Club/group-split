using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// The endpoints over a real HTTP pipeline. Everything else in this project calls a
/// service directly, which skips routing, model binding, the authorization policies and
/// the validation filter — and two of the defects fixed on this branch lived exactly
/// there. These go through the wire.
/// </summary>
public class EndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static StringContent PatchBody(params (string Op, string Path, object? Value)[] operations) =>
        new(JsonSerializer.Serialize(
                operations.Select(operation => new
                {
                    op = operation.Op,
                    path = operation.Path,
                    value = operation.Value
                })),
            Encoding.UTF8,
            "application/json-patch+json");

    private async Task<Guid> CreateGroup(string name = "Trip")
    {
        var response = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = name }, Json, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(
            Json, TestContext.Current.CancellationToken);

        return created.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateTransaction(decimal amount = 10m)
    {
        var response = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow
        }, Json, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(
            Json, TestContext.Current.CancellationToken);

        return created.GetProperty("id").GetGuid();
    }

    // ---- The authorization boundary -------------------------------------------------

    /// <summary>
    /// Every group carries RequireAuthorization. A test that calls the services directly
    /// cannot see whether it was ever applied.
    /// </summary>
    [Theory]
    [InlineData("/groups")]
    [InlineData("/transactions")]
    [InlineData("/transactions/summary")]
    [InlineData("/transactions/shares")]
    [InlineData("/transactions/shares/summary")]
    [InlineData("/users/me")]
    public async Task An_anonymous_request_is_refused(string route)
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The collection routes answer a GET, so the sweep above covers them; a single
    /// unsupported verb before authentication runs, which would otherwise make this look
    /// like an open route.
    /// </summary>
    [Fact]
    public async Task An_anonymous_request_for_a_rule_is_refused()
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.GetAsync($"/split-rules/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The archive routes are a POST and a DELETE, so the sweep above -- which asks with a
    /// GET -- cannot reach them.
    /// </summary>
    [Fact]
    public async Task An_anonymous_request_to_archive_is_refused()
    {
        using var anonymous = _host.AnonymousClient();
        var route = $"/groups/{Guid.NewGuid()}/archive";

        var archive = await anonymous.PostAsync(route, content: null, TestContext.Current.CancellationToken);
        var unarchive = await anonymous.DeleteAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, archive.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unarchive.StatusCode);
    }

    [Fact]
    public async Task Another_members_group_cannot_be_archived()
    {
        var groupId = await CreateGroup("Private");

        using var stranger = _host.ClientForAnotherUser();

        var archive = await stranger.PostAsync($"/groups/{groupId}/archive", content: null,
            TestContext.Current.CancellationToken);
        var unarchive = await stranger.DeleteAsync($"/groups/{groupId}/archive",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unarchive.StatusCode);
    }

    /// <summary>
    /// Archiving is the caller's own view of the group, so it shows on what they read back
    /// and on their listing -- and, in the service tests, on nobody else's.
    /// </summary>
    [Fact]
    public async Task Archiving_a_group_shows_on_it_and_unarchiving_takes_it_back()
    {
        var groupId = await CreateGroup();

        var archived = await Client.PostAsync($"/groups/{groupId}/archive", content: null,
            TestContext.Current.CancellationToken);
        archived.EnsureSuccessStatusCode();

        var afterArchive = await archived.Content.ReadFromJsonAsync<GroupResponse>(
            Json, TestContext.Current.CancellationToken);
        Assert.True(afterArchive!.IsArchive);

        var listed = await Client.GetFromJsonAsync<List<GroupResponse>>(
            "/groups", Json, TestContext.Current.CancellationToken);
        Assert.True(listed!.Single(g => g.Id == groupId).IsArchive);

        var unarchived = await Client.DeleteAsync($"/groups/{groupId}/archive",
            TestContext.Current.CancellationToken);
        unarchived.EnsureSuccessStatusCode();

        var afterUnarchive = await unarchived.Content.ReadFromJsonAsync<GroupResponse>(
            Json, TestContext.Current.CancellationToken);
        Assert.False(afterUnarchive!.IsArchive);
    }

    [Fact]
    public async Task An_authenticated_caller_is_provisioned_on_first_sight()
    {
        var response = await Client.GetAsync("/users/me", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var me = await response.Content.ReadFromJsonAsync<JsonElement>(
            Json, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, me.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// The defect this branch fixed, now checked where a client would meet it: the details
    /// route used to hand back any transaction to any signed-in caller who knew its id.
    /// </summary>
    [Fact]
    public async Task Another_members_transaction_is_not_found_over_http()
    {
        var transactionId = await CreateTransaction();

        using var stranger = _host.ClientForAnotherUser();
        var response = await stranger.GetAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task My_own_transaction_is_found()
    {
        var transactionId = await CreateTransaction(12.34m);

        var response = await Client.GetAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var details = await response.Content.ReadFromJsonAsync<TransactionDetailsResponse>(
            Json, TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal(12.34m, details.Amount);
    }

    [Fact]
    public async Task Another_members_group_is_not_readable_over_http()
    {
        var groupId = await CreateGroup("Private");

        using var stranger = _host.ClientForAnotherUser();

        foreach (var route in new[]
                 {
                     $"/groups/{groupId}", $"/groups/{groupId}/members",
                     $"/categories?groupId={groupId}", $"/split-rules?groupId={groupId}",
                     $"/groups/{groupId}/transactions",
                     $"/groups/{groupId}/transactions/summary"
                 })
        {
            var response = await stranger.GetAsync(route, TestContext.Current.CancellationToken);

            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                    or HttpStatusCode.NoContent or HttpStatusCode.OK,
                $"{route} answered {(int)response.StatusCode}");

            // An OK here must not carry the other member's group.
            if (response.StatusCode is HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.DoesNotContain("Private", body, StringComparison.Ordinal);
            }
        }
    }

    // ---- Validation on the way in ---------------------------------------------------

    // Body validation on the create routes is deliberately not asserted here. In .NET 10
    // minimal-API validation is a source-generated interceptor on the AddValidation() call
    // site, so it exists only in the API assembly's own Program.cs. A host assembled from
    // the outside, as this one is, gets the registration without the interceptor and the
    // annotations never run. What the annotations themselves do is covered by
    // ValidationAttributeTests, and the PATCH routes below validate through PatchedModel,
    // which is ordinary code and does run here.

    /// <summary>
    /// The other defect this branch fixed. The PATCH routes validated nothing, because the
    /// framework validates an endpoint's parameters and the parameter here is the patch
    /// document. Before the fix this returned a success and stored the value.
    /// </summary>
    [Fact]
    public async Task Patching_a_transaction_to_fractions_of_a_cent_is_rejected()
    {
        var transactionId = await CreateTransaction();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/amount", 10.005m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patching_a_transaction_name_away_is_rejected()
    {
        var transactionId = await CreateTransaction();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/name", null)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patching_a_transaction_past_the_name_limit_is_rejected()
    {
        var transactionId = await CreateTransaction();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/name", new string('x', 125))),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_patch_still_goes_through()
    {
        var transactionId = await CreateTransaction();

        var response = await Client.PatchAsync($"/transactions/{transactionId}",
            PatchBody(("replace", "/name", "Brunch"), ("replace", "/amount", 12.50m)),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var details = await Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{transactionId}", Json, TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal("Brunch", details.Name);
        Assert.Equal(12.50m, details.Amount);
    }

    [Fact]
    public async Task Patching_a_group_name_away_is_rejected()
    {
        var groupId = await CreateGroup();

        var response = await Client.PatchAsync($"/groups/{groupId}",
            PatchBody(("replace", "/name", null)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- The ordinary paths ---------------------------------------------------------

    [Fact]
    public async Task A_created_group_comes_back_in_the_listing()
    {
        var groupId = await CreateGroup("Ski trip");

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            "/groups", Json, TestContext.Current.CancellationToken);

        Assert.Contains(listing.EnumerateArray(),
            element => element.GetProperty("id").GetGuid() == groupId);
    }

    [Fact]
    public async Task A_created_transaction_comes_back_in_the_listing()
    {
        var transactionId = await CreateTransaction();

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            "/transactions", Json, TestContext.Current.CancellationToken);

        Assert.Contains(listing.GetProperty("items").EnumerateArray(),
            element => element.GetProperty("id").GetGuid() == transactionId);
    }

    /// <summary>
    /// The listing is a page, and says which one: a client that only ever read the rows
    /// would have no way to know there were more.
    /// </summary>
    [Fact]
    public async Task The_listing_is_a_page_and_says_how_much_there_is_to_page_through()
    {
        await CreateTransaction(10m);
        await CreateTransaction(20m);
        await CreateTransaction(30m);

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            "/transactions?Page=1&PageSize=2", Json, TestContext.Current.CancellationToken);

        Assert.Equal(2, listing.GetProperty("items").GetArrayLength());
        Assert.Equal(1, listing.GetProperty("page").GetInt32());
        Assert.Equal(2, listing.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, listing.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task The_second_page_holds_what_the_first_one_did_not()
    {
        await CreateTransaction(10m);
        await CreateTransaction(20m);
        await CreateTransaction(30m);

        var second = await Client.GetFromJsonAsync<JsonElement>(
            "/transactions?Page=2&PageSize=2", Json, TestContext.Current.CancellationToken);

        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task A_listing_sorts_by_the_key_the_query_names()
    {
        await CreateTransaction(30m);
        await CreateTransaction(10m);
        await CreateTransaction(20m);

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            "/transactions?SortBy=amount&SortDescending=false", Json, TestContext.Current.CancellationToken);

        var amounts = listing.GetProperty("items").EnumerateArray()
            .Select(element => element.GetProperty("amount").GetDecimal()).ToList();

        Assert.Equal([10m, 20m, 30m], amounts);
    }

    [Fact]
    public async Task A_sort_key_the_listing_does_not_offer_is_refused()
    {
        var response = await Client.GetAsync("/transactions?SortBy=nonsense",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_summary_counts_and_totals_the_whole_match_not_the_page()
    {
        await CreateTransaction(10m);
        await CreateTransaction(20m);
        await CreateTransaction(30m);

        var summary = await Client.GetFromJsonAsync<TransactionSummaryResponse>(
            "/transactions/summary", Json, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(3, summary.Count);
        Assert.Equal(60m, summary.Total);
    }

    /// <summary>
    /// The share listing over the wire, and the routing question it raises: "shares" sits
    /// where an id goes, and only the guid constraint on the neighbouring route keeps the
    /// two apart.
    /// </summary>
    [Fact]
    public async Task A_share_listing_is_a_page_of_what_the_caller_owes_a_part_of()
    {
        var transactionId = await CreateTransaction(10m);

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            "/transactions/shares", Json, TestContext.Current.CancellationToken);

        var row = Assert.Single(listing.GetProperty("items").EnumerateArray());

        Assert.Equal(transactionId, row.GetProperty("id").GetGuid());
        Assert.Equal(10m, row.GetProperty("amount").GetDecimal());
        // A personal expense is nobody else's to share, so the whole of it is the caller's.
        Assert.Equal(10m, row.GetProperty("share").GetDecimal());
        Assert.True(row.GetProperty("paidByYou").GetBoolean());
        Assert.Equal(1, listing.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_share_listing_sorts_by_the_one_key_only_it_offers()
    {
        await CreateTransaction(30m);
        await CreateTransaction(10m);
        await CreateTransaction(20m);

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            "/transactions/shares?SortBy=share&SortDescending=false",
            Json, TestContext.Current.CancellationToken);

        var shares = listing.GetProperty("items").EnumerateArray()
            .Select(element => element.GetProperty("share").GetDecimal()).ToList();

        Assert.Equal([10m, 20m, 30m], shares);
    }

    [Fact]
    public async Task A_share_summary_keeps_what_is_owed_apart_from_what_was_paid_for()
    {
        await CreateTransaction(10m);
        await CreateTransaction(20m);

        var summary = await Client.GetFromJsonAsync<ExpenseShareSummaryResponse>(
            "/transactions/shares/summary", Json, TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.Count);
        Assert.Equal(30m, summary.Total);
        Assert.Equal(30m, summary.Share);
        // Both are the caller's own, so none of it is a debt. A single total would have
        // said they owed thirty pounds to themselves.
        Assert.Equal(0m, summary.OwedToOthers);
    }

    [Fact]
    public async Task A_transaction_that_does_not_exist_is_a_404()
    {
        var response = await Client.GetAsync($"/transactions/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_category_and_the_split_rule_it_defaults_to_can_be_created_and_read_back()
    {
        var groupId = await CreateGroup();

        var rule = await Client.PostAsJsonAsync("/split-rules", new CreateSplitRuleRequest
        {
            GroupId = groupId,
            Name = "Household",
            Definition = new PayerSplitRuleDto()
        }, Json, TestContext.Current.CancellationToken);

        rule.EnsureSuccessStatusCode();

        var ruleId = (await rule.Content.ReadFromJsonAsync<JsonElement>(
            Json, TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var category = await Client.PostAsJsonAsync("/categories", new CreateCategoryRequest
        {
            GroupId = groupId,
            Name = "Groceries",
            DefaultSplitRuleId = ruleId
        }, Json, TestContext.Current.CancellationToken);

        category.EnsureSuccessStatusCode();

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            $"/categories?groupId={groupId}", Json, TestContext.Current.CancellationToken);

        var groceries = Assert.Single(listing.EnumerateArray(),
            element => element.GetProperty("name").GetString() == "Groceries");
        Assert.Equal("Household", groceries.GetProperty("defaultSplitRuleName").GetString());
    }

    [Fact]
    public async Task A_transaction_can_be_deleted()
    {
        var transactionId = await CreateTransaction();

        var deleted = await Client.DeleteAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);
        deleted.EnsureSuccessStatusCode();

        var afterwards = await Client.GetAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);
    }

    /// <summary>
    /// The delete is refused by the service throwing, and the exception handler turns that
    /// into the 404 a client can act on. It used to escape as a 500; the full shape of the
    /// answer is pinned in <c>ProblemResponseTest</c>, this only keeps the boundary.
    /// </summary>
    [Fact]
    public async Task Another_member_deleting_my_transaction_is_not_found()
    {
        var transactionId = await CreateTransaction();

        using var stranger = _host.ClientForAnotherUser();

        var refusal = await stranger.DeleteAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, refusal.StatusCode);

        // The transaction is untouched and still mine.
        var mine = await Client.GetAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
    }
}

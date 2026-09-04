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
    [InlineData("/users/me")]
    public async Task An_anonymous_request_is_refused(string route)
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// /rules answers only on an id, so it is asked for one. Routing rejects an
    /// unsupported verb before authentication runs, which would otherwise make this look
    /// like an open route.
    /// </summary>
    [Fact]
    public async Task An_anonymous_request_for_a_rule_is_refused()
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.GetAsync($"/rules/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
                     $"/groups/{groupId}/rules", $"/groups/{groupId}/transactions"
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

        Assert.Contains(listing.EnumerateArray(),
            element => element.GetProperty("id").GetGuid() == transactionId);
    }

    [Fact]
    public async Task A_transaction_that_does_not_exist_is_a_404()
    {
        var response = await Client.GetAsync($"/transactions/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_rule_can_be_created_and_read_back()
    {
        var groupId = await CreateGroup();

        var response = await Client.PostAsJsonAsync("/rules", new CreateRuleRequest
        {
            GroupId = groupId,
            Category = "Groceries",
            Version = new PersonalRuleVersionDto()
        }, Json, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        var listing = await Client.GetFromJsonAsync<JsonElement>(
            $"/groups/{groupId}/rules", Json, TestContext.Current.CancellationToken);

        Assert.Contains(listing.EnumerateArray(),
            element => element.GetProperty("category").GetString() == "Groceries");
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
    /// The delete is refused, which is the point — but it is refused by throwing, and the
    /// API maps no exception to a status code, so what reaches a client is a 500 where a
    /// 404 belongs. TestServer surfaces the exception rather than swallowing it into a
    /// response, which is what makes it visible here at all.
    /// </summary>
    [Fact]
    public async Task Another_member_deleting_my_transaction_is_refused_but_as_a_500()
    {
        var transactionId = await CreateTransaction();

        using var stranger = _host.ClientForAnotherUser();

        var refusal = await Assert.ThrowsAnyAsync<Exception>(() =>
            stranger.DeleteAsync($"/transactions/{transactionId}",
                TestContext.Current.CancellationToken));

        Assert.Contains("not found", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // The transaction is untouched and still mine.
        var mine = await Client.GetAsync($"/transactions/{transactionId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
    }
}

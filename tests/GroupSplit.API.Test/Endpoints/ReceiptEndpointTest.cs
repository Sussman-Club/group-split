using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// The receipt routes over a real HTTP pipeline.
/// </summary>
/// <remarks>
/// Everything else written for this feature calls the services directly, which skips
/// routing, model binding, the authorization policies and the validation filter -- and the
/// defect that made this file necessary lived in exactly that gap. A line's division binds
/// from an enum on the wire, and out-of-range values were reaching the service and silently
/// dropping that line's money out of the weights; the fix is an attribute that does nothing
/// at all unless the generated validation filter runs on these routes. Nothing but a request
/// can tell you whether it does.
/// </remarks>
public class ReceiptEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<Guid> AGroupExpense(decimal amount = 30m)
    {
        var group = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Flat" }, Json, Ct);

        group.EnsureSuccessStatusCode();

        var groupId = (await group.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("id").GetGuid();

        var expense = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId
        }, Json, Ct);

        expense.EnsureSuccessStatusCode();

        return (await expense.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("/transactions/{0}/receipt")]
    [InlineData("/transactions/{0}/receipt/preview")]
    public async Task An_anonymous_request_is_refused(string route)
    {
        using var anonymous = _host.AnonymousClient();

        var response = await anonymous.GetAsync(
            string.Format(route, Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A line names who had it and nothing else, so a bill whose lines name nobody is stored
    /// and refuses to divide.
    /// </summary>
    /// <remarks>
    /// The three tests this replaces were about a line saying it divided some other way --
    /// an enum on the wire, its out-of-range values, and its names. There is no other way
    /// now: an itemised division is "everybody owes what they had", and a bill that wants an
    /// even split wants a category with an even rule.
    /// </remarks>
    [Fact]
    public async Task A_bill_nobody_has_claimed_is_stored_and_refuses_to_divide()
    {
        var expense = await AGroupExpense();

        var saved = await Client.PutAsJsonAsync($"/transactions/{expense}/receipt", new
        {
            subtotal = 30m,
            total = 30m,
            items = new[] { new { name = "Everything", totalPrice = 30m } }
        }, Json, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await saved.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal(1, body.GetProperty("unclaimedItemCount").GetInt32());
        Assert.False(body.GetProperty("canDivide").GetBoolean());

        var divided = await Client.PostAsync($"/transactions/{expense}/receipt/divide", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, divided.StatusCode);
    }

    /// <summary>
    /// A bill that does not describe its expense is refused with the receipt's own code, and
    /// carries both figures -- not the generic shares-do-not-sum refusal.
    /// </summary>
    [Fact]
    public async Task A_bill_that_is_not_the_expenses_money_is_refused_by_name()
    {
        var expense = await AGroupExpense(amount: 30m);

        var response = await Client.PutAsJsonAsync($"/transactions/{expense}/receipt", new
        {
            subtotal = 40m,
            total = 40m,
            items = new[] { new { name = "Everything", totalPrice = 40m, split = "Evenly" } }
        }, Json, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal("RECEIPT_DOES_NOT_ADD_UP", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// A bill on an expense that does not exist, or belongs to somebody else, is a 404 and
    /// not a 403: whether it exists is not the caller's to learn.
    /// </summary>
    [Fact]
    public async Task A_bill_on_an_expense_nobody_can_see_is_not_found()
    {
        var response = await Client.GetAsync($"/transactions/{Guid.NewGuid()}/receipt", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// An expense with no bill says so, rather than answering an empty one.
    /// </summary>
    [Fact]
    public async Task An_expense_with_no_bill_answers_not_found()
    {
        var expense = await AGroupExpense();

        var response = await Client.GetAsync($"/transactions/{expense}/receipt", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal("RECEIPT_NOT_FOUND", problem.GetProperty("code").GetString());
    }
}

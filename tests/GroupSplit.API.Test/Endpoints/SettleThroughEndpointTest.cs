using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// Settling through a day over the wire: the query parameters bind, the body fields bind,
/// and a settlement's detail carries what it covers.
/// </summary>
/// <remarks>
/// The arithmetic is <c>SettleThroughTest</c>'s. These are about the parts a direct service
/// call skips -- <c>[AsParameters]</c> binding, the offset's range check, and the detail
/// endpoint attaching the coverage that the transaction service itself knows nothing about.
/// </remarks>
public class SettleThroughEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<(Guid GroupId, Guid Me, Guid Other)> AGroupOfTwo()
    {
        var me = (await Client.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        using var otherClient = _host.ClientForAnotherUser();
        var other = (await otherClient.GetFromJsonAsync<UserInfo>("/users/me", Json, Ct))!;

        var groupResponse = await Client.PostAsJsonAsync("/groups",
            new CreateGroupRequest { Name = "Flat" }, Json, Ct);
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

    private async Task AnExpense(Guid groupId, Guid payerId, decimal amount, DateTimeOffset date)
    {
        var created = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            DateTime = date,
            GroupId = groupId,
            PaidByUserId = payerId
        }, Json, Ct);
        created.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_balances_through_a_day_leave_out_what_came_after_it_on_the_callers_calendar()
    {
        var (groupId, me, other) = await AGroupOfTwo();

        await AnExpense(groupId, me, 100m, new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        // 22:00 on 31 August in New York, and already 1 September in UTC.
        await AnExpense(groupId, me, 40m, new DateTimeOffset(2026, 9, 1, 2, 0, 0, TimeSpan.Zero));

        var inUtc = await Client.GetFromJsonAsync<UserGroupBalanceResponse>(
            $"/groups/{groupId}/balances?through=2026-08-31", Json, Ct);
        var inNewYork = await Client.GetFromJsonAsync<UserGroupBalanceResponse>(
            $"/groups/{groupId}/balances?through=2026-08-31&utcOffset=-240", Json, Ct);

        Assert.Equal(-50m, inUtc!.NetBalances.Single(balance => balance.UserId == other).Balance);
        Assert.Equal(-70m, inNewYork!.NetBalances.Single(balance => balance.UserId == other).Balance);
    }

    [Theory]
    [InlineData("/groups/{0}/balances?through=2026-08-31&utcOffset=900")]
    [InlineData("/users/me/settlement-plan?through=2026-08-31&utcOffset=-900")]
    public async Task An_offset_no_time_zone_has_is_refused(string route)
    {
        var (groupId, _, _) = await AGroupOfTwo();

        var response = await Client.GetAsync(string.Format(route, groupId), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_settlement_made_through_a_day_reads_back_what_it_covers()
    {
        var (groupId, me, _) = await AGroupOfTwo();

        await AnExpense(groupId, me, 100m, new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

        var settled = await Client.PostAsJsonAsync($"/groups/{groupId}/settle-up",
            new SettleUpRequest { Through = new DateOnly(2026, 8, 31), UtcOffset = -240 }, Json, Ct);
        settled.EnsureSuccessStatusCode();

        var history = await Client.GetFromJsonAsync<JsonElement>("/users/me/settlements", Json, Ct);
        var row = history.GetProperty("items")[0];
        Assert.Equal("2026-08-31", row.GetProperty("coversThrough").GetString());

        var details = await Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{row.GetProperty("id").GetGuid()}?utcOffset=-240", Json, Ct);

        Assert.NotNull(details!.Coverage);
        Assert.Equal(new DateOnly(2026, 8, 31), details.Coverage.Through);
        Assert.Equal(0m, details.Coverage.StillOwed);
    }

    [Fact]
    public async Task A_settlements_day_can_be_patched_and_cleared()
    {
        var (groupId, _, other) = await AGroupOfTwo();

        var settled = await Client.PostAsJsonAsync($"/groups/{groupId}/settle", new SettleRequest
        {
            UserId = other,
            Amount = 10m,
            Direction = SettlementDirection.TheyPaidYou
        }, Json, Ct);
        settled.EnsureSuccessStatusCode();

        var history = await Client.GetFromJsonAsync<JsonElement>("/users/me/settlements", Json, Ct);
        var id = history.GetProperty("items")[0].GetProperty("id").GetGuid();

        var patched = await Client.PatchAsync($"/transactions/{id}", new StringContent(
            """[{"op":"replace","path":"/coversThrough","value":"2026-08-31"}]""",
            Encoding.UTF8, "application/json-patch+json"), Ct);
        patched.EnsureSuccessStatusCode();
        var afterPatch = await patched.Content.ReadFromJsonAsync<TransactionDetailsResponse>(Json, Ct);
        Assert.Equal(new DateOnly(2026, 8, 31), afterPatch!.Coverage?.Through);

        var cleared = await Client.PatchAsync($"/transactions/{id}", new StringContent(
            """[{"op":"replace","path":"/coversThrough","value":null}]""",
            Encoding.UTF8, "application/json-patch+json"), Ct);
        cleared.EnsureSuccessStatusCode();
        var afterClear = await cleared.Content.ReadFromJsonAsync<TransactionDetailsResponse>(Json, Ct);
        Assert.Null(afterClear!.Coverage);
    }
}

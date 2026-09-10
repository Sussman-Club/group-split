using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// A pending invitee over the wire: in the members listing, marked, and choosable in a
/// split.
/// </summary>
/// <remarks>
/// The service tests next door reach past routing and the serializer, and this is where
/// most of it actually matters to a client. Every screen that offers a choice of person --
/// who paid, whose share, who a rule names -- reads <c>GET /groups/{id}/members</c>, so
/// whether an invitee is in that array, and whether the array says which of them have
/// joined, is the whole difference between a marked choice and a person the app presents
/// as a member who never signed up.
/// <para>
/// And withdrawing an invitation used to answer 204. It answers a body now, because there
/// is something to say about the money -- a 204 would leave a client with nothing to show
/// for a change to somebody's balance.
/// </para>
/// </remarks>
public class PendingInviteeEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> CreateGroup(string name = "The flat")
    {
        var response = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = name }, Json, Ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("id").GetGuid();
    }

    private async Task<GroupInvitationResponse> Invite(Guid groupId, string name)
    {
        var response = await Client.PostAsJsonAsync(
            $"/groups/{groupId}/invitations",
            new InviteToGroupRequest { Names = [name] },
            Json, Ct);

        response.EnsureSuccessStatusCode();

        var pending = await response.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct);

        return pending!.Single(invitation => invitation.Name == name);
    }

    private async Task<UserInfo[]> Members(Guid groupId)
    {
        var response = await Client.GetAsync($"/groups/{groupId}/members", Ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<UserInfo[]>(Json, Ct))!;
    }

    [Fact]
    public async Task The_members_listing_carries_the_invited_and_says_they_have_not_joined()
    {
        var group = await CreateGroup();
        var invitation = await Invite(group, "Carlos");

        var members = await Members(group);

        Assert.Equal(2, members.Length);

        var waiting = members.Single(person => person.Id == invitation.ParticipantUserId);

        Assert.True(waiting.IsPendingInvitee);

        // The name the group gave them, and no address: they have no profile yet.
        Assert.Equal("Carlos", waiting.FullName);
        Assert.Null(waiting.Email);

        Assert.All(members.Where(person => person.Id != waiting.Id),
            person => Assert.False(person.IsPendingInvitee));
    }

    [Fact]
    public async Task An_expense_can_give_one_of_them_a_share()
    {
        var group = await CreateGroup();
        var invitation = await Invite(group, "Carlos");

        var me = (await Members(group)).Single(person => !person.IsPendingInvitee);

        var response = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            GroupId = group,
            Amount = 90m,
            DateTime = DateTimeOffset.UtcNow,
            Name = "Rent",
            Splits =
            [
                new SplitInput { UserId = me.Id, Amount = 60m },
                new SplitInput { UserId = invitation.ParticipantUserId, Amount = 30m }
            ]
        }, Json, Ct);

        response.EnsureSuccessStatusCode();

        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("id").GetGuid();

        var details = await Client.GetFromJsonAsync<TransactionDetailsResponse>(
            $"/transactions/{id}", Json, Ct);

        var theirs = details!.Splits.Single(split => split.UserId == invitation.ParticipantUserId);

        Assert.Equal(30m, theirs.Amount);
        Assert.True(theirs.IsPendingInvitee);
        Assert.Equal(90m, details.Splits.Sum(split => split.Amount));
    }

    [Fact]
    public async Task Withdrawing_answers_with_what_became_of_the_money()
    {
        var group = await CreateGroup();
        var invitation = await Invite(group, "Carlos");

        var expense = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            GroupId = group,
            Amount = 90m,
            DateTime = DateTimeOffset.UtcNow,
            Name = "Rent"
        }, Json, Ct);

        expense.EnsureSuccessStatusCode();

        var response = await Client.DeleteAsync($"/groups/{group}/invitations/{invitation.Id}", Ct);

        response.EnsureSuccessStatusCode();

        var closed = await response.Content.ReadFromJsonAsync<InvitationClosedResponse>(Json, Ct);

        Assert.NotNull(closed);
        Assert.Equal(InvitationOutcome.Withdrawn, closed.Outcome);
        Assert.Equal("Carlos", closed.Name);
        Assert.Equal(1, closed.SharesMoved);
        Assert.Equal(45m, closed.AmountOwed);
        Assert.NotNull(closed.AbsorbedByUserName);

        Assert.Single(await Members(group));
    }
}

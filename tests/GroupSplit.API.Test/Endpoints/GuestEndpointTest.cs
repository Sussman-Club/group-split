using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// A guest over the wire: in the members listing, marked, choosable in a split, and
/// reachable through the guest routes by their user id.
/// </summary>
/// <remarks>
/// The service tests next door reach past routing and the serializer, and this is where
/// most of it actually matters to a client. Every screen that offers a choice of person --
/// who paid, whose share, who a rule names -- reads <c>GET /groups/{id}/members</c>, so
/// whether an invitee is in that array, and whether the array says which of them have
/// joined, is the whole difference between a marked choice and a person the app presents
/// as a member who never signed up.
/// <para>
/// The flag is written under both of its names, <c>isGuest</c> and <c>isPendingInvitee</c>,
/// for one release, so a client built before the rename still reads it.
/// </para>
/// </remarks>
public class GuestEndpointTest : IAsyncLifetime
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
    public async Task The_members_listing_carries_guests_and_marks_them_under_both_names()
    {
        var group = await CreateGroup();
        var invitation = await Invite(group, "Carlos");

        var members = await Members(group);

        Assert.Equal(2, members.Length);

        var waiting = members.Single(person => person.Id == invitation.ParticipantUserId);

        Assert.True(waiting.IsGuest);

        // The name the group gave them, and no address: they have no profile.
        Assert.Equal("Carlos", waiting.FullName);
        Assert.Null(waiting.Email);

        Assert.All(members.Where(person => person.Id != waiting.Id),
            person => Assert.False(person.IsGuest));

        // Both names on the wire, for clients built before the rename.
        var raw = await Client.GetFromJsonAsync<JsonElement>($"/groups/{group}/members", Json, Ct);
        var row = raw.EnumerateArray().Single(person => person.GetProperty("id").GetGuid() == waiting.Id);

        Assert.True(row.GetProperty("isGuest").GetBoolean());
        Assert.True(row.GetProperty("isPendingInvitee").GetBoolean());
    }

    [Fact]
    public async Task An_expense_can_give_one_of_them_a_share()
    {
        var group = await CreateGroup();
        var invitation = await Invite(group, "Carlos");

        var me = (await Members(group)).Single(person => !person.IsGuest);

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
        Assert.True(theirs.IsGuest);
        Assert.Equal(90m, details.Splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A name longer than the columns behind it is refused, not written.
    /// </summary>
    /// <remarks>
    /// The name goes into two 64-character columns -- the invitation's, and the stand-in
    /// account's first name. Bounded only there, an over-long one reached the database and
    /// came back as a 500 with a trace id: a rejected input reported as a fault. The
    /// annotation makes it the 400 it always was, with the field named.
    /// </remarks>
    [Fact]
    public async Task A_name_too_long_for_its_column_is_a_refusal_and_not_a_fault()
    {
        var group = await CreateGroup();

        var response = await Client.PostAsJsonAsync(
            $"/groups/{group}/invitations",
            new InviteToGroupRequest { Names = [new string('a', InviteToGroupRequest.NameLength + 1)] },
            Json, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal(ErrorCodes.ValidationFailed, problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("errors", out _));

        // And nothing was written: no invitation, and no stand-in for one.
        Assert.Single(await Members(group));
    }

    /// <summary>The bound is on each entry, so a workable name beside a long one still lands.</summary>
    [Fact]
    public async Task A_name_at_the_limit_is_accepted()
    {
        var group = await CreateGroup();

        var name = new string('a', InviteToGroupRequest.NameLength);

        var response = await Client.PostAsJsonAsync(
            $"/groups/{group}/invitations",
            new InviteToGroupRequest { Names = [name] },
            Json, Ct);

        response.EnsureSuccessStatusCode();

        var pending = await response.Content.ReadFromJsonAsync<GroupInvitationResponse[]>(Json, Ct);

        Assert.Equal(name, Assert.Single(pending!).Name);
    }

    /// <summary>
    /// The old route still answers, and now only stops the link: the guest and what is
    /// recorded for them stay, so the counts are zero.
    /// </summary>
    [Fact]
    public async Task Withdrawing_by_the_old_route_stops_the_link_and_keeps_the_guest()
    {
        var group = await CreateGroup();
        var invitation = await Invite(group, "Carlos");

        var response = await Client.DeleteAsync($"/groups/{group}/invitations/{invitation.Id}", Ct);

        response.EnsureSuccessStatusCode();

        var closed = await response.Content.ReadFromJsonAsync<InvitationClosedResponse>(Json, Ct);

        Assert.NotNull(closed);
        Assert.Equal(InvitationOutcome.Withdrawn, closed.Outcome);
        Assert.Equal(0, closed.SharesMoved);
        Assert.Null(closed.AbsorbedByUserName);

        Assert.Equal(2, (await Members(group)).Length);
    }

    [Fact]
    public async Task Guests_are_added_without_a_link_and_issued_one_on_request()
    {
        var group = await CreateGroup();

        var added = await Client.PostAsJsonAsync($"/groups/{group}/guests",
            new AddGuestsRequest { Names = ["Pat"] }, Json, Ct);
        added.EnsureSuccessStatusCode();

        var pat = Assert.Single((await added.Content.ReadFromJsonAsync<GroupGuestResponse[]>(Json, Ct))!);
        Assert.False(pat.HasLink);
        Assert.Null(pat.Token);

        var issued = await Client.PostAsJsonAsync($"/groups/{group}/guests/{pat.UserId}/link",
            new IssueGuestLinkRequest(), Json, Ct);
        issued.EnsureSuccessStatusCode();

        var linked = (await issued.Content.ReadFromJsonAsync<GroupGuestResponse>(Json, Ct))!;
        Assert.True(linked.HasLink);
        Assert.NotNull(linked.Token);

        var listed = Assert.Single((await Client.GetFromJsonAsync<GroupGuestResponse[]>($"/groups/{group}/guests", Json, Ct))!);
        Assert.Equal(linked.Token, listed.Token);

        var stopped = await Client.DeleteAsync($"/groups/{group}/guests/{pat.UserId}/link", Ct);
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);

        Assert.False(Assert.Single((await Client.GetFromJsonAsync<GroupGuestResponse[]>($"/groups/{group}/guests", Json, Ct))!).HasLink);
    }

    [Fact]
    public async Task A_guest_route_for_somebody_who_is_not_a_guest_is_a_404_with_its_code()
    {
        var group = await CreateGroup();
        var me = Assert.Single(await Members(group));

        var response = await Client.PostAsJsonAsync($"/groups/{group}/guests/{me.Id}/link",
            new IssueGuestLinkRequest(), Json, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal(ErrorCodes.GroupGuestNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_square_guest_is_removed_through_the_members_route()
    {
        var group = await CreateGroup();

        var added = await Client.PostAsJsonAsync($"/groups/{group}/guests",
            new AddGuestsRequest { Names = ["Pat"] }, Json, Ct);
        var pat = Assert.Single((await added.Content.ReadFromJsonAsync<GroupGuestResponse[]>(Json, Ct))!);

        var response = await Client.DeleteAsync($"/groups/{group}/members/{pat.UserId}", Ct);
        response.EnsureSuccessStatusCode();

        Assert.Single(await Members(group));

        // Kept as somebody who has gone, and marked as having been a guest.
        var past = (await Client.GetFromJsonAsync<UserInfo[]>($"/groups/{group}/past-members", Json, Ct))!;
        var removed = Assert.Single(past, person => person.Id == pat.UserId);
        Assert.True(removed.IsPastMember);
        Assert.True(removed.IsGuest);
    }

    /// <summary>
    /// "Removed guest" comes from the history, not from being a stand-in. One left behind by
    /// an invitation from before guests -- in the ledger, with no guest row and no history --
    /// is a past participant, and was never removed from anything.
    /// </summary>
    [Fact]
    public async Task Only_a_guest_the_group_removed_is_marked_as_one_among_past_members()
    {
        var group = await CreateGroup();

        var added = await Client.PostAsJsonAsync($"/groups/{group}/guests",
            new AddGuestsRequest { Names = ["Pat"] }, Json, Ct);
        var pat = Assert.Single((await added.Content.ReadFromJsonAsync<GroupGuestResponse[]>(Json, Ct))!);
        (await Client.DeleteAsync($"/groups/{group}/members/{pat.UserId}", Ct)).EnsureSuccessStatusCode();

        var expense = await Client.PostAsJsonAsync("/transactions", new CreateTransactionRequest
        {
            GroupId = group, Amount = 10m, DateTime = DateTimeOffset.UtcNow, Name = "Bread"
        }, Json, Ct);
        expense.EnsureSuccessStatusCode();
        var expenseId = (await expense.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("id").GetGuid();

        // Written past the services: the leftover only old data can produce.
        Guid leftover;
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<GroupSplit.Data.AppDbContext>();
            var user = new GroupSplit.Data.Entities.User
            {
                FirstName = "Old Dani", Kind = GroupSplit.Data.Entities.UserKind.StandIn
            };
            context.Add(user);
            context.Add(new GroupSplit.Data.Entities.TransactionSplit
            {
                TransactionId = expenseId, UserId = user.Id, Amount = 0m
            });
            await context.SaveChangesAsync(Ct);
            leftover = user.Id;
        }

        var past = (await Client.GetFromJsonAsync<UserInfo[]>($"/groups/{group}/past-members", Json, Ct))!;

        Assert.True(Assert.Single(past, person => person.Id == pat.UserId).IsGuest);
        Assert.False(Assert.Single(past, person => person.Id == leftover).IsGuest);
    }
}

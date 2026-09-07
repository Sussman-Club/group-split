using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Endpoints;

/// <summary>
/// A link made by one person and followed by another, over the wire.
/// </summary>
/// <remarks>
/// The service tests next door reach past routing, the authorization policies and the
/// status codes, and those are most of what a join link is: whether a group with no link
/// reads as an empty list or as a failure decides whether the members page renders a button
/// or an error, and which status a dead link answers with decides whether the person
/// holding it is told anything useful. Two clients, because the whole point is that the
/// second person is somebody the group had no way to reach.
/// </remarks>
public class JoinLinkEndpointTest : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiEndpointHost _host = null!;
    private HttpClient Client => _host.Client;

    public async ValueTask InitializeAsync() => _host = await ApiEndpointHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> CreateGroup(string name = "Lisbon")
    {
        var response = await Client.PostAsJsonAsync("/groups", new CreateGroupRequest { Name = name }, Json, Ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("id").GetGuid();
    }

    private async Task<GroupJoinLinkResponse> CreateLink(Guid groupId)
    {
        var response = await Client.PostAsync($"/groups/{groupId}/join-links", null, Ct);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GroupJoinLinkResponse>(Json, Ct))!;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task A_group_with_no_link_reads_as_an_empty_list()
    {
        var group = await CreateGroup();

        var response = await Client.GetAsync($"/groups/{group}/join-links", Ct);
        var links = await response.Content.ReadFromJsonAsync<GroupJoinLinkResponse[]>(Json, Ct);

        // Not an error and not a 204: having no link is an ordinary state of a group, and
        // the members page renders "make one" from this rather than from a failure.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(links!);
    }

    [Fact]
    public async Task A_link_is_made_and_read_back()
    {
        var group = await CreateGroup();

        var made = await CreateLink(group);

        Assert.NotEmpty(made.Token);
        Assert.Equal("Lisbon", made.GroupName);

        var response = await Client.GetAsync($"/groups/{group}/join-links", Ct);
        var links = await response.Content.ReadFromJsonAsync<GroupJoinLinkResponse[]>(Json, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(made.Token, Assert.Single(links!).Token);
    }

    [Fact]
    public async Task Somebody_who_was_never_invited_follows_the_link_and_joins()
    {
        var group = await CreateGroup();
        var link = await CreateLink(group);

        var them = _host.ClientForAnotherUser();

        var described = await them.GetFromJsonAsync<JoinLinkResponse>($"/invitations/links/{link.Token}", Json, Ct);

        Assert.Equal(group, described!.GroupId);
        Assert.Equal("Lisbon", described.GroupName);
        Assert.False(described.AlreadyAMember);

        var accepted = await them.PostAsync($"/invitations/links/{link.Token}/accept", null, Ct);
        var joined = await accepted.Content.ReadFromJsonAsync<JoinedGroupResponse>(Json, Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False(joined!.AlreadyAMember);

        // Their own list, which is the thing they opened the link to end up in.
        var theirs = await them.GetFromJsonAsync<GroupResponse[]>("/groups", Json, Ct);

        Assert.Equal(group, Assert.Single(theirs!).Id);
    }

    [Fact]
    public async Task Following_it_twice_joins_once()
    {
        var group = await CreateGroup();
        var link = await CreateLink(group);

        var them = _host.ClientForAnotherUser();

        await them.PostAsync($"/invitations/links/{link.Token}/accept", null, Ct);

        var again = await them.PostAsync($"/invitations/links/{link.Token}/accept", null, Ct);
        var joined = await again.Content.ReadFromJsonAsync<JoinedGroupResponse>(Json, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(joined!.AlreadyAMember);

        var members = await Client.GetFromJsonAsync<UserInfo[]>($"/groups/{group}/members", Json, Ct);

        Assert.Equal(2, members!.Length);
    }

    [Fact]
    public async Task A_revoked_link_is_a_conflict_that_names_the_reason()
    {
        var group = await CreateGroup();
        var link = await CreateLink(group);

        var revoked = await Client.DeleteAsync($"/groups/{group}/join-links", Ct);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        var them = _host.ClientForAnotherUser();
        var response = await them.GetAsync($"/invitations/links/{link.Token}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.GroupJoinLinkRevoked, await CodeOf(response));
    }

    [Fact]
    public async Task A_token_nobody_issued_is_a_404()
    {
        var response = await Client.GetAsync("/invitations/links/nothing-here", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.GroupJoinLinkNotFound, await CodeOf(response));
    }

    [Fact]
    public async Task A_link_for_somebody_elses_group_is_a_404()
    {
        var group = await CreateGroup();

        var them = _host.ClientForAnotherUser();

        var response = await them.PostAsync($"/groups/{group}/join-links", null, Ct);

        // The same answer any other read of a group they are not in gets: whether it exists
        // is not theirs to learn.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.GroupNotFound, await CodeOf(response));
    }

    [Fact]
    public async Task Following_a_link_needs_an_account()
    {
        var group = await CreateGroup();
        var link = await CreateLink(group);

        var anonymous = _host.AnonymousClient();

        // Which is what sends somebody with no account to sign up first, and is the reason
        // the web app hangs on to where they were going.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/invitations/links/{link.Token}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/invitations/links/{link.Token}/accept", null, Ct)).StatusCode);
    }
}

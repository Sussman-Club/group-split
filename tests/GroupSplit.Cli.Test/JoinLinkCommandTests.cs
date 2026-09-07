using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The join link from the terminal, driven the way a caller drives it.
/// </summary>
/// <remarks>
/// Two things here are the CLI's own and not the API's. The URL a person opens is composed
/// here, from the server origin this client was pointed at -- so a configuration that names
/// only the API gets the token and is told why. And what somebody has to hand is a link, not
/// the token inside it, so the invitee's commands take either.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class JoinLinkCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    private static readonly Guid Trip = Guid.NewGuid();

    private const string Token = "Zm9vYmFyYmF6cXV4MTIzNDU2Nzg";

    public JoinLinkCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    private static object ALink(string token = Token) => new
    {
        id = Guid.NewGuid(),
        groupId = Trip,
        groupName = "Weekend in Lisbon",
        token,
        createdByUserName = "Daniel Rivero",
        createdAt = DateTimeOffset.UtcNow,
        expiresAt = DateTimeOffset.UtcNow.AddDays(14)
    };

    [Fact]
    public async Task A_group_with_no_link_is_answered_rather_than_failed()
    {
        _api.Returns($"/api/groups/{Trip}/join-links", Array.Empty<object>());

        var result = await Cli.RunAsync("groups", "link", "show", Trip.ToString());

        // A group nobody has made a link for is an ordinary group. A script asking after
        // one wants an answer, not an exit code to branch on.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.False(result.Json.GetProperty("hasLink").GetBoolean());
    }

    [Fact]
    public async Task Without_a_server_origin_the_token_is_given_and_the_reason_said()
    {
        _api.Returns($"/api/groups/{Trip}/join-links", new[] { ALink() });

        var result = await Cli.RunAsync("groups", "link", "show", Trip.ToString());

        Assert.Equal(Token, result.Json.GetProperty("token").GetString());

        // GROUPSPLIT_API_URL says where the API is and nothing about where a browser would
        // find the app, so there is no URL to be had and none is invented. The writer drops
        // nulls, so "no URL" is the member not being there.
        Assert.False(result.Json.TryGetProperty("url", out _));

        var text = await Cli.RunAsync("groups", "link", "show", Trip.ToString(), "--output", "text");

        Assert.Contains("config set server", text.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_server_origin_the_link_is_a_URL_somebody_can_open()
    {
        var origin = new Uri(_api.BaseAddress).GetLeftPart(UriPartial.Authority);

        _environment.Set(EnvironmentVariables.ApiUrl, null);
        _environment.Set(EnvironmentVariables.Server, origin);

        // The path the web app forwards token-bearing clients on, which is where the API
        // actually is once only an origin is configured.
        _api.Returns($"/native/api/groups/{Trip}/join-links", new[] { ALink() });

        var result = await Cli.RunAsync("groups", "link", "show", Trip.ToString());

        Assert.Equal($"{origin}/join/{Token}", result.Json.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Replacing_a_link_that_exists_asks_first_and_says_what_breaks()
    {
        _api.Returns($"/api/groups/{Trip}/join-links", new[] { ALink() });

        var result = await Cli.RunAsync("groups", "link", "create", Trip.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("groups.link.create", result.Json.GetProperty("action").GetString());

        Assert.Contains(
            result.Json.GetProperty("changes").EnumerateArray(),
            change => change.GetString()!.Contains("stops working", StringComparison.Ordinal));

        // Only the read happened: the link somebody has already shared is still good.
        Assert.DoesNotContain(_api.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task Making_a_groups_first_link_asks_nothing()
    {
        // The read and the write share an address and mean different things, so each verb
        // is answered on its own.
        _api.Returns($"/api/groups/{Trip}/join-links", Array.Empty<object>(), method: "GET");
        _api.Returns($"/api/groups/{Trip}/join-links", ALink(), method: "POST");

        var result = await Cli.RunAsync("groups", "link", "create", Trip.ToString());

        // There is nothing to lose, and stopping a script to say so would be a prompt about
        // no consequence at all.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(Token, result.Json.GetProperty("token").GetString());
        Assert.Contains(_api.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task Replacing_one_with_yes_goes_through()
    {
        _api.Returns($"/api/groups/{Trip}/join-links", new[] { ALink("old-token") }, method: "GET");
        _api.Returns($"/api/groups/{Trip}/join-links", ALink(), method: "POST");

        var result = await Cli.RunAsync("groups", "link", "create", Trip.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(Token, result.Json.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Turning_a_link_off_asks_first()
    {
        _api.Returns($"/api/groups/{Trip}",
            new { id = Trip, name = "Weekend in Lisbon", memberCount = 3, isArchive = false });

        var result = await Cli.RunAsync("groups", "link", "revoke", Trip.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("groups.link.revoke", result.Json.GetProperty("action").GetString());
        Assert.Equal($"groupsplit groups link revoke {Trip} --yes",
            result.Json.GetProperty("confirmCommand").GetString());

        Assert.DoesNotContain(_api.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task A_whole_URL_is_accepted_where_a_token_is_asked_for()
    {
        _api.Returns($"/api/invitations/links/{Token}",
            new
            {
                groupId = Trip,
                groupName = "Weekend in Lisbon",
                memberCount = 3,
                createdByUserName = "Daniel Rivero",
                expiresAt = DateTimeOffset.UtcNow.AddDays(14),
                alreadyAMember = false
            });

        // What a person has in their clipboard is the link they were sent, not the token
        // buried at the end of it.
        var result = await Cli.RunAsync(
            "invitations", "link", $"https://groupsplit.example.com/join/{Token}");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Weekend in Lisbon", result.Json.GetProperty("groupName").GetString());
        Assert.Contains(_api.Requests, request => request.Path == $"/api/invitations/links/{Token}");
    }

    [Fact]
    public async Task Following_a_link_to_a_group_you_are_in_succeeds()
    {
        _api.Returns($"/api/invitations/links/{Token}/accept",
            new { groupId = Trip, groupName = "Weekend in Lisbon", memberCount = 3, alreadyAMember = true });

        var result = await Cli.RunAsync("invitations", "join", Token);

        // Not a failure: following the same link twice is what a URL in a chat thread gets,
        // and there is one membership at the end of it either way.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(result.Json.GetProperty("alreadyAMember").GetBoolean());
    }

    [Fact]
    public async Task A_withdrawn_link_carries_its_own_code_to_the_caller()
    {
        _api.Problem($"/api/invitations/links/{Token}/accept", 409,
            "GROUP_JOIN_LINK_REVOKED", "That join link has been withdrawn by the group.");

        var result = await Cli.RunAsync("invitations", "join", Token);

        Assert.NotEqual(ExitCodes.Success, result.ExitCode);

        // The code, not the wording: a script branching on "which way is this link dead"
        // has exactly this to branch on.
        Assert.Equal("GROUP_JOIN_LINK_REVOKED", result.Error.GetProperty("code").GetString());
    }
}

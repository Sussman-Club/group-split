using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// A group's guests from the terminal: adding them, listing them, and the link a member can
/// send so the real person takes their place.
/// </summary>
/// <remarks>
/// Guests are addressed by their user id -- the same id their shares, payments and
/// repayments name -- so the id an agent reads from <c>groups guests</c> is the one it puts
/// in a split, passes to <c>settle</c>, and removes them by.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class GuestCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _api = new();

    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly Guid Pat = Guid.NewGuid();

    public GuestCommandTests()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, _api.BaseAddress);
        _environment.Set(EnvironmentVariables.Token, "test-token");
    }

    public void Dispose()
    {
        _api.Dispose();
        _environment.Dispose();
    }

    /// <summary>When a stubbed link runs out: a date the output can be searched for.</summary>
    private static readonly DateTimeOffset Expiry = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static object AGuest(string? token = null) => new
    {
        userId = Pat,
        groupId = Trip,
        groupName = "Ski trip",
        name = "Pat",
        addedByUserName = "Daniel Rivero",
        addedAt = DateTimeOffset.UtcNow,
        hasLink = token is not null,
        token,
        linkIssuedByUserName = token is null ? null : "Daniel Rivero",
        linkIssuedAt = token is null ? (DateTimeOffset?)null : Expiry.AddDays(-30),
        linkExpiresAt = token is null ? (DateTimeOffset?)null : Expiry,
        linkExpired = false
    };

    /// <summary>A guest whose link went out and ran out unclaimed: no token, and saying so.</summary>
    private static object AGuestWhoseLinkExpired() => new
    {
        userId = Pat,
        groupId = Trip,
        groupName = "Ski trip",
        name = "Pat",
        addedByUserName = "Daniel Rivero",
        addedAt = DateTimeOffset.UtcNow,
        hasLink = false,
        token = (string?)null,
        linkIssuedByUserName = "Daniel Rivero",
        linkIssuedAt = Expiry.AddDays(-30),
        linkExpiresAt = Expiry,
        linkExpired = true
    };

    [Fact]
    public async Task Add_guest_sends_the_names_and_asks_for_no_link_by_default()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest() }, method: "POST");

        var result = await Cli.RunAsync("groups", "add-guest", Trip.ToString(), "Pat");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var body = _api.Requests.Single(request => request.Method == "POST").Json;

        Assert.Equal("Pat", body.GetProperty("names")[0].GetString());
        Assert.False(body.GetProperty("issueLinks").GetBoolean());

        // The id an agent needs next is in the answer.
        Assert.Equal(Pat, result.Json[0].GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Add_guest_with_link_asks_for_one()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-pat") }, method: "POST");

        await Cli.RunAsync("groups", "add-guest", Trip.ToString(), "Pat", "--link");

        var body = _api.Requests.Single(request => request.Method == "POST").Json;

        Assert.True(body.GetProperty("issueLinks").GetBoolean());
    }

    [Fact]
    public async Task Guests_lists_them_by_user_id_and_says_who_has_a_link()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest() });

        var result = await Cli.RunAsync("groups", "guests", Trip.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(Pat.ToString(), result.Stdout);
        Assert.Contains("Pat", result.Stdout);
        Assert.Contains("none", result.Stdout);
    }

    /// <summary>
    /// The text listing says a link is out without printing the token: it is a credential,
    /// and a table is what gets pasted into a chat.
    /// </summary>
    [Fact]
    public async Task Guests_text_output_never_prints_a_bare_token()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-secret") });

        var result = await Cli.RunAsync("groups", "guests", Trip.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain("tok-secret", result.Stdout);
        Assert.Contains("yes", result.Stdout);
    }

    [Fact]
    public async Task Guests_says_until_when_a_link_works()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-pat") });

        var result = await Cli.RunAsync("groups", "guests", Trip.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains($"until {Expiry.ToLocalTime():yyyy-MM-dd}", result.Stdout);
        Assert.DoesNotContain("expired", result.Stdout);
    }

    /// <summary>
    /// A link that ran out unclaimed is shown as expired, with the day, and points at the
    /// command that issues another -- not as "none", which would hide that one went out.
    /// </summary>
    [Fact]
    public async Task Guests_shows_a_link_that_ran_out_as_expired()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuestWhoseLinkExpired() });

        var result = await Cli.RunAsync("groups", "guests", Trip.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(Pat.ToString(), result.Stdout);
        Assert.Contains($"Pat's link expired on {Expiry.ToLocalTime():yyyy-MM-dd}.", result.Stdout);
        Assert.Contains("groupsplit groups guest-link create <group-id> <user-id>", result.Stdout);

        // And the JSON says it in fields an agent can test.
        var json = await Cli.RunAsync("groups", "guests", Trip.ToString());

        Assert.True(json.Json[0].GetProperty("linkExpired").GetBoolean());
        Assert.False(json.Json[0].GetProperty("hasLink").GetBoolean());
    }

    /// <summary>
    /// Replacing a link that already ran out loses nothing -- the old URL answers nobody --
    /// so <c>--new</c> does not stop to ask, and plain <c>create</c> issues a new one too.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Guest_link_create_issues_a_new_link_for_an_expired_one_without_asking(bool replace)
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuestWhoseLinkExpired() });
        _api.Returns($"/api/groups/{Trip}/guests/{Pat}/link", AGuest("tok-new"), method: "POST");

        string[] args = ["groups", "guest-link", "create", Trip.ToString(), Pat.ToString()];

        var result = await Cli.RunAsync(replace ? [.. args, "--new"] : args);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("tok-new", result.Json.GetProperty("token").GetString());
        Assert.Equal(Expiry, result.Json.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(replace,
            _api.Requests.Single(request => request.Method == "POST").Json.GetProperty("replace").GetBoolean());
    }

    /// <summary>
    /// Issuing a first link, or reading back the one that is out, destroys nothing, so it
    /// does not stop to ask.
    /// </summary>
    [Fact]
    public async Task Guest_link_create_issues_a_first_link_without_asking()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest() });
        _api.Returns($"/api/groups/{Trip}/guests/{Pat}/link", AGuest("tok-pat"), method: "POST");

        var result = await Cli.RunAsync("groups", "guest-link", "create", Trip.ToString(), Pat.ToString());

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("tok-pat", result.Json.GetProperty("token").GetString());

        var body = _api.Requests.Single(request => request.Method == "POST").Json;
        Assert.False(body.GetProperty("replace").GetBoolean());
    }

    /// <summary>Replacing a link that is out kills the one already sent, so it asks.</summary>
    [Fact]
    public async Task Guest_link_create_new_asks_before_replacing_a_link_that_is_out()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-old") });

        var result = await Cli.RunAsync("groups", "guest-link", "create", Trip.ToString(), Pat.ToString(), "--new");

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Equal("groups.guest-link.create", result.Json.GetProperty("action").GetString());
        Assert.Contains("Pat", result.Json.GetProperty("summary").GetString());
        Assert.DoesNotContain(_api.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task Guest_link_create_new_replaces_once_confirmed()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-old") });
        _api.Returns($"/api/groups/{Trip}/guests/{Pat}/link", AGuest("tok-new"), method: "POST");

        var result = await Cli.RunAsync("groups", "guest-link", "create", Trip.ToString(), Pat.ToString(),
            "--new", "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(_api.Requests.Single(request => request.Method == "POST").Json.GetProperty("replace").GetBoolean());
        Assert.Equal("tok-new", result.Json.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Guest_link_revoke_asks_first_and_says_the_guest_stays()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-pat") });

        var result = await Cli.RunAsync("groups", "guest-link", "revoke", Trip.ToString(), Pat.ToString());

        Assert.Equal(ExitCodes.ConfirmationRequired, result.ExitCode);
        Assert.Contains(result.Json.GetProperty("changes").EnumerateArray(),
            change => change.GetString()!.Contains("stays a guest"));
        Assert.DoesNotContain(_api.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Guest_link_revoke_sends_the_delete_once_confirmed()
    {
        _api.Returns($"/api/groups/{Trip}/guests", new[] { AGuest("tok-pat") });
        _api.NoContent($"/api/groups/{Trip}/guests/{Pat}/link", method: "DELETE");

        var result = await Cli.RunAsync("groups", "guest-link", "revoke", Trip.ToString(), Pat.ToString(), "--yes");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_api.Requests, request => request.Method == "DELETE" &&
                                                  request.Path == $"/api/groups/{Trip}/guests/{Pat}/link");
    }

    /// <summary>
    /// Somebody who is not a guest has no link to issue or stop, and is told so before any
    /// write -- with the command that lists who is.
    /// </summary>
    [Fact]
    public async Task A_guest_link_for_somebody_who_is_not_a_guest_is_refused_before_any_write()
    {
        _api.Returns($"/api/groups/{Trip}/guests", Array.Empty<object>());

        var result = await Cli.RunAsync("groups", "guest-link", "revoke", Trip.ToString(), Pat.ToString(), "--yes");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal(GroupSplit.Shared.Errors.ErrorCodes.GroupGuestNotFound, result.Error.GetProperty("code").GetString());
        Assert.Contains("groups guests", result.Error.GetProperty("remediation").GetString());
        Assert.DoesNotContain(_api.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Balances_mark_a_guest()
    {
        _api.Returns($"/api/groups/{Trip}/balances", new
        {
            netBalances = new[]
            {
                new { userId = Pat, userName = "Pat", amountPaid = 0m, amountOwed = 45m, balance = -45m, isGuest = true }
            },
            owedToYou = Array.Empty<object>(),
            youOwed = Array.Empty<object>()
        });

        var result = await Cli.RunAsync("groups", "balances", Trip.ToString(), "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("(guest)", result.Stdout);
    }
}

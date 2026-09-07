using GroupSplit.Cli.Auth;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The edges of the auth path: the poll loop's error arms, a hostile or broken identity
/// server, and the two predicates that have to agree about what an empty token means.
/// Every one of these was raised in review on code that could not be exercised by hand.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class AuthHardeningTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _idp = new();

    private const string Realm = "/realms/group-split";

    public AuthHardeningTests()
    {
        _environment.Set(EnvironmentVariables.Authority, Authority);
        _environment.Set(EnvironmentVariables.ApiUrl, _idp.BaseAddress);
        Discovery();
    }

    public void Dispose()
    {
        _idp.Dispose();
        _environment.Dispose();
    }

    private string Authority => _idp.BaseAddress.Replace("/api", Realm);

    // ---- an empty token is not a session ---------------------------------------------

    [Fact]
    public async Task An_empty_token_is_treated_as_no_token_by_status_and_by_requests_alike()
    {
        // What a workflow exports when the secret it maps does not resolve. Reporting a
        // session here while every request fails sends the reader looking somewhere else.
        _environment.Set(EnvironmentVariables.Token, "");

        var status = await Cli.RunAsync("auth", "status");
        Assert.Equal("signed_out", status.Json.GetProperty("status").GetString());

        var list = await Cli.RunAsync("groups", "list");
        Assert.Equal(ExitCodes.AuthRequired, list.ExitCode);
        Assert.Empty(_idp.Requests.Where(r => r.Path == "/api/groups"));
    }

    [Fact]
    public async Task Whitespace_is_not_a_token_either()
    {
        _environment.Set(EnvironmentVariables.Token, "   ");

        Assert.Equal("signed_out", (await Cli.RunAsync("auth", "status")).Json.GetProperty("status").GetString());
    }

    // ---- the poll loop ---------------------------------------------------------------

    [Fact]
    public async Task An_expired_device_code_says_to_start_again()
    {
        Device();
        _idp.Returns($"{Realm}/protocol/openid-connect/token", new { error = "expired_token" }, 400);

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Contains("expired", result.Error.GetProperty("error").GetString());
        Assert.Contains("auth login", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task An_unrecognised_error_from_the_token_endpoint_is_reported_not_swallowed()
    {
        Device();
        _idp.Returns($"{Realm}/protocol/openid-connect/token",
            new { error = "invalid_scope", error_description = "Scope not permitted." }, 400);

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal("Scope not permitted.", result.Error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_poll_stops_at_the_deadline_rather_than_forever()
    {
        // expires_in 0 puts the deadline at now, so the loop must exit on its first check
        // rather than polling for a code that can never be approved. It also pins that an
        // explicit zero stays distinguishable from an absent value, which takes a default.
        _idp.Returns($"{Realm}/protocol/openid-connect/auth/device", new
        {
            device_code = "d", user_code = "ABCD-EFGH", verification_uri = "https://example.com/device",
            expires_in = 0, interval = 0
        });
        _idp.Returns($"{Realm}/protocol/openid-connect/token", new { error = "authorization_pending" }, 400);

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Contains("Timed out", result.Error.GetProperty("error").GetString());
    }

    // ---- a server that is not what it claims -----------------------------------------

    [Fact]
    public async Task Endpoints_on_another_origin_are_refused_before_any_credential_is_sent()
    {
        // A tampered document naming somewhere else would otherwise receive the device code
        // and, on the token endpoint, the refresh token.
        _idp.Returns($"{Realm}/.well-known/openid-configuration", new
        {
            issuer = Authority,
            token_endpoint = "https://attacker.example.com/token",
            device_authorization_endpoint = $"{Authority}/protocol/openid-connect/auth/device"
        });

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Contains("attacker.example.com", result.Error.GetProperty("error").GetString());
        Assert.Empty(_idp.Requests.Where(r => r.Path.Contains("auth/device")));
    }

    [Fact]
    public async Task An_issuer_on_another_origin_is_refused_too()
    {
        _idp.Returns($"{Realm}/.well-known/openid-configuration", new
        {
            issuer = "https://attacker.example.com/realms/group-split",
            token_endpoint = $"{Authority}/protocol/openid-connect/token"
        });

        Assert.Equal(ExitCodes.AuthRequired, (await Cli.RunAsync("auth", "login", "--no-browser")).ExitCode);
    }

    [Fact]
    public async Task A_proxy_answering_with_html_is_a_server_problem_not_a_cli_bug()
    {
        _idp.Html($"{Realm}/.well-known/openid-configuration", 502, "<html>Bad Gateway</html>");

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        // The catch-all would have claimed this was a defect in the CLI and sent the
        // reader looking in the wrong place.
        Assert.NotEqual(ErrorCodes.Internal, result.Error.GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.ServerUnreachable, result.Error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_token_endpoint_answering_with_html_is_reported_as_such()
    {
        Device();
        _idp.Html($"{Realm}/protocol/openid-connect/token", 502, "<html>Bad Gateway</html>");

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ErrorCodes.AuthFailed, result.Error.GetProperty("code").GetString());
        Assert.Contains("not JSON", result.Error.GetProperty("error").GetString());
    }

    // ---- logout ----------------------------------------------------------------------

    [Fact]
    public async Task Logout_reports_success_when_the_local_credential_is_gone()
    {
        // Unreachable identity server, with the credential stored against it: discovery
        // throws before the revoke is ever attempted, which is the case the catch inside
        // RevokeAsync could never see.
        var unreachable = new Uri("http://127.0.0.1:1/realms/group-split");
        _environment.Set(EnvironmentVariables.Authority, unreachable.ToString());

        var store = new TokenStore();
        store.Save(unreachable, "cli", new StoredCredential
        {
            AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        var result = await Cli.RunAsync("auth", "logout");

        // The local half is what the user asked for and it happened, so this must not
        // report a failure for work it already did.
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("could not be ended", result.Stderr);
        Assert.Null(store.Get(unreachable, "cli"));
    }

    // ---- refresh ---------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_response_without_a_refresh_token_keeps_the_one_already_held()
    {
        var store = new TokenStore();
        var authority = new Uri(Authority);

        store.Save(authority, "cli", new StoredCredential
        {
            AccessToken = "stale", RefreshToken = "keep-me", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });

        // No refresh_token in the response: nulling the stored one would strand the user at
        // the next expiry with nothing to point at.
        _idp.Returns($"{Realm}/protocol/openid-connect/token", new { access_token = "fresh", expires_in = 3600 });
        _idp.Returns("/api/groups", Array.Empty<object>());

        Assert.Equal(ExitCodes.Success, (await Cli.RunAsync("groups", "list")).ExitCode);
        Assert.Equal("keep-me", store.Get(authority, "cli")!.RefreshToken);
    }

    private void Discovery() => _idp.Returns($"{Realm}/.well-known/openid-configuration", new
    {
        issuer = Authority,
        token_endpoint = $"{Authority}/protocol/openid-connect/token",
        device_authorization_endpoint = $"{Authority}/protocol/openid-connect/auth/device",
        end_session_endpoint = $"{Authority}/protocol/openid-connect/logout"
    });

    private void Device() => _idp.Returns($"{Realm}/protocol/openid-connect/auth/device", new
    {
        device_code = "d", user_code = "ABCD-EFGH", verification_uri = "https://example.com/device",
        expires_in = 600, interval = 0
    });
}

using System.Text;
using System.Text.Json;
using GroupSplit.Cli.Auth;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The device grant, driven end to end against a stand-in identity server: discovery, the
/// device authorization request, polling, and what ends up on disk.
/// <para>
/// Worth testing at this level because none of it can be exercised by hand without a browser
/// and a real realm, so a break here would otherwise surface as a failed sign-in on someone's
/// machine rather than in CI.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class DeviceLoginTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly StubApi _idp = new();

    private const string Realm = "/realms/group-split";

    public DeviceLoginTests()
    {
        var authority = _idp.BaseAddress.Replace("/api", Realm);

        _environment.Set(EnvironmentVariables.Authority, authority);
        _environment.Set(EnvironmentVariables.ApiUrl, _idp.BaseAddress);

        _idp.Returns($"{Realm}/.well-known/openid-configuration", new
        {
            issuer = authority,
            token_endpoint = $"{authority}/protocol/openid-connect/token",
            device_authorization_endpoint = $"{authority}/protocol/openid-connect/auth/device",
            end_session_endpoint = $"{authority}/protocol/openid-connect/logout"
        });
    }

    public void Dispose()
    {
        _idp.Dispose();
        _environment.Dispose();
    }

    [Fact]
    public async Task A_successful_login_stores_the_credential_and_reports_the_user()
    {
        Device();
        Token(Jwt("anabel", DateTimeOffset.UtcNow.AddHours(1)));

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("signed_in", result.Json.GetProperty("status").GetString());
        Assert.Equal("anabel", result.Json.GetProperty("username").GetString());

        // The instructions are guidance, not the result, so they belong on stderr.
        Assert.Contains("ABCD-EFGH", result.Stderr);

        var stored = new TokenStore().Get(new Uri(Authority), "cli");
        Assert.Equal("refresh-me", stored!.RefreshToken);
        Assert.Equal("anabel", stored.Username);
    }

    [Fact]
    public async Task After_signing_in_status_reports_it_and_the_api_carries_the_token()
    {
        Device();
        Token(Jwt("anabel", DateTimeOffset.UtcNow.AddHours(1)));
        await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal("signed_in", (await Cli.RunAsync("auth", "status")).Json.GetProperty("status").GetString());

        _idp.Returns("/api/groups", Array.Empty<object>());
        await Cli.RunAsync("groups", "list");

        Assert.Contains(_idp.Requests, r => r.Path == "/api/groups" && r.Authorization!.StartsWith("Bearer "));
    }

    [Fact]
    public async Task A_denied_request_says_so_rather_than_polling_forever()
    {
        Device();
        _idp.Returns($"{Realm}/protocol/openid-connect/token",
            new { error = "access_denied", error_description = "User denied." }, 400);

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal(ErrorCodes.AuthFailed, result.Error.GetProperty("code").GetString());
        Assert.Contains("auth login", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task A_realm_without_the_device_grant_names_the_client_to_fix()
    {
        // What every environment looks like until the `cli` client is added to the realm.
        _idp.Returns($"{Realm}/protocol/openid-connect/auth/device", new { error = "invalid_client" }, 401);

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Contains("'cli' client", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task An_unreachable_identity_server_is_reported_with_the_url()
    {
        _environment.Set(EnvironmentVariables.Authority, "http://127.0.0.1:1/realms/group-split");

        var result = await Cli.RunAsync("auth", "login", "--no-browser");

        Assert.Equal(ErrorCodes.ServerUnreachable, result.Error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_expired_credential_is_refreshed_without_asking_anyone()
    {
        new TokenStore().Save(new Uri(Authority), "cli", new StoredCredential
        {
            AccessToken = Jwt("anabel", DateTimeOffset.UtcNow.AddHours(-1)),
            RefreshToken = "refresh-me",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });

        Token(Jwt("anabel", DateTimeOffset.UtcNow.AddHours(1)));
        _idp.Returns("/api/groups", Array.Empty<object>());

        var result = await Cli.RunAsync("groups", "list");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(_idp.Requests, r => r.Path.EndsWith("/protocol/openid-connect/token"));
    }

    [Fact]
    public async Task A_spent_refresh_token_is_discarded_so_the_next_run_says_sign_in()
    {
        var store = new TokenStore();

        store.Save(new Uri(Authority), "cli", new StoredCredential
        {
            AccessToken = "stale",
            RefreshToken = "revoked",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        });

        _idp.Returns($"{Realm}/protocol/openid-connect/token", new { error = "invalid_grant" }, 400);

        var result = await Cli.RunAsync("groups", "list");

        Assert.Equal(ExitCodes.AuthRequired, result.ExitCode);
        Assert.Equal(ErrorCodes.AuthExpired, result.Error.GetProperty("code").GetString());

        // Discarded, so the next command reports "sign in" instead of failing the same way.
        Assert.Null(store.Get(new Uri(Authority), "cli"));
    }

    [Fact]
    public async Task Logout_forgets_the_credential()
    {
        Device();
        Token(Jwt("anabel", DateTimeOffset.UtcNow.AddHours(1)));
        await Cli.RunAsync("auth", "login", "--no-browser");

        _idp.Returns($"{Realm}/protocol/openid-connect/logout", new { });

        var result = await Cli.RunAsync("auth", "logout");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Null(new TokenStore().Get(new Uri(Authority), "cli"));
    }

    private string Authority => _idp.BaseAddress.Replace("/api", Realm);

    /// <summary>interval 0 so the poll loop does not sleep; the RFC's default is five seconds.</summary>
    private void Device() => _idp.Returns($"{Realm}/protocol/openid-connect/auth/device", new
    {
        device_code = "device-code",
        user_code = "ABCD-EFGH",
        verification_uri = "https://example.com/device",
        verification_uri_complete = "https://example.com/device?user_code=ABCD-EFGH",
        expires_in = 600,
        interval = 0
    });

    private void Token(string accessToken) => _idp.Returns($"{Realm}/protocol/openid-connect/token", new
    {
        access_token = accessToken,
        refresh_token = "refresh-me",
        expires_in = 3600
    });

    private static string Jwt(string username, DateTimeOffset expires)
    {
        var payload = JsonSerializer.Serialize(new
        {
            preferred_username = username,
            sub = Guid.NewGuid().ToString(),
            exp = expires.ToUnixTimeSeconds()
        });

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"header.{encoded}.signature";
    }
}

using System.Text.Json;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The commands behind the promise that no server URL is compiled in. These are the ones a
/// new user runs first, so a broken one is felt before anything else works.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class ConfigCommandTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public async Task Set_then_list_shows_the_endpoints_derived_from_one_origin()
    {
        var set = await Cli.RunAsync("config", "set", "server", "https://groupsplit.example.com");
        Assert.Equal(ExitCodes.Success, set.ExitCode);

        var list = await Cli.RunAsync("config", "list");

        Assert.Equal("https://groupsplit.example.com/api", list.Json.GetProperty("api").GetString());
        Assert.Equal(
            "https://groupsplit.example.com/idp/realms/group-split",
            list.Json.GetProperty("authority").GetString());
    }

    [Fact]
    public async Task Set_writes_to_the_named_profile_and_leaves_the_default_alone()
    {
        await Cli.RunAsync("config", "set", "server", "https://prod.example.com");
        await Cli.RunAsync("config", "set", "server", "https://staging.example.com", "--profile", "staging");

        Assert.Equal(
            "https://prod.example.com/api",
            (await Cli.RunAsync("config", "list")).Json.GetProperty("api").GetString());

        Assert.Equal(
            "https://staging.example.com/api",
            (await Cli.RunAsync("config", "list", "--profile", "staging")).Json.GetProperty("api").GetString());
    }

    [Fact]
    public async Task Get_and_unset_round_trip()
    {
        await Cli.RunAsync("config", "set", "server", "https://example.com");

        Assert.Equal(
            "https://example.com",
            (await Cli.RunAsync("config", "get", "server")).Json.GetProperty("value").GetString());

        Assert.Equal(ExitCodes.Success, (await Cli.RunAsync("config", "unset", "server")).ExitCode);

        // Null members are omitted rather than written as null, matching the API's own
        // problem details. `jq -r .value` reads absent and null alike, so a consumer sees
        // no difference; an unknown key is an error, so absence is never ambiguous.
        var after = await Cli.RunAsync("config", "get", "server");
        Assert.False(after.Json.TryGetProperty("value", out _));
    }

    [Theory]
    [InlineData("apiUrl")]
    [InlineData("api-url")]
    [InlineData("API_URL")]
    public async Task Key_names_are_forgiving_about_case_and_separators(string key)
    {
        var result = await Cli.RunAsync("config", "set", key, "https://api.example.com");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("https://api.example.com", (await Cli.RunAsync("config", "get", key)).Json.GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_unknown_key_lists_the_ones_that_exist()
    {
        var result = await Cli.RunAsync("config", "set", "srever", "https://example.com");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("server", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task Profiles_marks_the_default_one()
    {
        await Cli.RunAsync("config", "set", "server", "https://a.example.com");
        await Cli.RunAsync("config", "set", "server", "https://b.example.com", "--profile", "other");

        var result = await Cli.RunAsync("config", "profiles", "--output", "text");

        Assert.Contains("default", result.Stdout);
        Assert.Contains("other", result.Stdout);
    }

    [Fact]
    public async Task Profiles_is_empty_rather_than_an_error_on_a_fresh_machine()
    {
        var result = await Cli.RunAsync("config", "profiles", "--output", "text");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("No profiles configured", result.Stdout);
    }

    [Fact]
    public async Task Path_prints_where_the_files_live()
    {
        var result = await Cli.RunAsync("config", "path");

        Assert.Contains("config.json", result.Json.GetProperty("configFile").GetString());
        Assert.Contains("credentials.json", result.Json.GetProperty("credentialsFile").GetString());
    }

    [Fact]
    public async Task With_nothing_configured_the_error_says_how_to_configure_it()
    {
        var result = await Cli.RunAsync("groups", "list");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Equal(ErrorCodes.ServerNotConfigured, result.Error.GetProperty("code").GetString());
        Assert.Contains("config set server", result.Error.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task An_unknown_profile_is_rejected_rather_than_silently_ignored()
    {
        await Cli.RunAsync("config", "set", "server", "https://a.example.com");

        var result = await Cli.RunAsync("groups", "list", "--profile", "nope");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
    }

    [Fact]
    public async Task Plain_http_to_somewhere_that_is_not_loopback_warns_once()
    {
        // Accepted, because the local Aspire Keycloak is http and cannot be otherwise. But
        // over http the bearer token crosses the network in the clear, and saying nothing
        // about that is how it goes unnoticed.
        var result = await Cli.RunAsync("groups", "list", "--server", "http://staging.internal");

        Assert.Contains("plain http", result.Stderr);
        Assert.Contains("credentials are sent unencrypted", result.Stderr);
    }

    [Fact]
    public async Task Loopback_over_http_says_nothing_because_it_never_leaves_the_machine()
    {
        var result = await Cli.RunAsync("groups", "list", "--server", "http://localhost:5001");

        Assert.DoesNotContain("plain http", result.Stderr);
    }

    [Fact]
    public async Task Https_says_nothing_either()
    {
        var result = await Cli.RunAsync("groups", "list", "--server", "https://groupsplit.example.com");

        Assert.DoesNotContain("plain http", result.Stderr);
    }

    [Fact]
    public async Task A_server_that_is_not_a_url_is_rejected_before_any_request()
    {
        var result = await Cli.RunAsync("groups", "list", "--server", "not-a-url");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
        Assert.Contains("scheme", result.Error.GetProperty("remediation").GetString());
    }
}

using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The server URL is a runtime input, never a compile-time constant, so these cover the
/// order the sources are consulted in and the two URLs derived from one origin.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class EndpointResolverTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();
    private readonly EndpointResolver _resolver;
    private readonly ConfigStore _store = new();

    public EndpointResolverTests() => _resolver = new EndpointResolver(_store);

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void Derives_api_and_authority_from_one_server_origin()
    {
        var endpoints = _resolver.Resolve("https://groupsplit.example.com", null);

        Assert.Equal("https://groupsplit.example.com/api", endpoints.Api.ToString());
        Assert.Equal("https://groupsplit.example.com/idp/realms/group-split", endpoints.Authority.ToString());
        Assert.Equal("cli", endpoints.ClientId);
    }

    [Fact]
    public void Keeps_a_sub_path_when_the_server_is_not_hosted_at_the_root()
    {
        var endpoints = _resolver.Resolve("https://example.com/groupsplit", null);

        Assert.Equal("https://example.com/groupsplit/api", endpoints.Api.ToString());
        Assert.Equal("https://example.com/groupsplit/idp/realms/group-split", endpoints.Authority.ToString());
    }

    [Fact]
    public void Trailing_slash_on_the_server_does_not_double_up()
    {
        Assert.Equal("https://example.com/api", _resolver.Resolve("https://example.com/", null).Api.ToString());
    }

    [Fact]
    public void Option_wins_over_environment_which_wins_over_config()
    {
        _store.Save(new CliConfigFile
        {
            Profiles = { ["default"] = new CliProfile { Server = "https://from-config.example.com" } }
        });

        Assert.Equal("https://from-config.example.com/api", _resolver.Resolve(null, null).Api.ToString());

        _environment.Set(EnvironmentVariables.Server, "https://from-env.example.com");
        Assert.Equal("https://from-env.example.com/api", _resolver.Resolve(null, null).Api.ToString());

        Assert.Equal(
            "https://from-flag.example.com/api",
            _resolver.Resolve("https://from-flag.example.com", null).Api.ToString());
    }

    [Fact]
    public void Explicit_api_and_authority_replace_the_derived_ones()
    {
        // The local Aspire arrangement: two origins on two ports, neither derivable from
        // the other, so the CLI has to be told both.
        _environment.Set(EnvironmentVariables.ApiUrl, "https://localhost:7043");
        _environment.Set(EnvironmentVariables.Authority, "http://localhost:8080/realms/group-split");

        var endpoints = _resolver.Resolve(null, null);

        Assert.Equal("https://localhost:7043/", endpoints.Api.ToString());
        Assert.Equal("http://localhost:8080/realms/group-split", endpoints.Authority.ToString());
    }

    [Fact]
    public void An_api_url_alone_is_a_complete_configuration()
    {
        // The CI and agent path: GROUPSPLIT_TOKEN means the identity server is never
        // contacted, so demanding its URL would reject a well-specified invocation.
        _environment.Set(EnvironmentVariables.ApiUrl, "https://localhost:7043");

        var endpoints = _resolver.Resolve(null, null);

        Assert.Equal("https://localhost:7043/", endpoints.Api.ToString());
        Assert.Null(endpoints.AuthorityOrNull);
    }

    [Fact]
    public void Asking_for_an_unconfigured_authority_explains_all_three_ways_to_supply_one()
    {
        _environment.Set(EnvironmentVariables.ApiUrl, "https://localhost:7043");

        var exception = Assert.Throws<CliException>(() => _resolver.Resolve(null, null).Authority);

        Assert.Equal(ExitCodes.InvalidInput, exception.ExitCode);
        Assert.Contains(EnvironmentVariables.Authority, exception.Error.Remediation);
        Assert.Contains(EnvironmentVariables.Token, exception.Error.Remediation);
    }

    [Fact]
    public void Named_profiles_are_resolved_independently()
    {
        _store.Save(new CliConfigFile
        {
            DefaultProfile = "prod",
            Profiles =
            {
                ["prod"] = new CliProfile { Server = "https://prod.example.com" },
                ["staging"] = new CliProfile { Server = "https://staging.example.com" }
            }
        });

        Assert.Equal("https://prod.example.com/api", _resolver.Resolve(null, null).Api.ToString());
        Assert.Equal("https://staging.example.com/api", _resolver.Resolve(null, "staging").Api.ToString());
    }

    [Fact]
    public void Missing_configuration_is_an_input_error_not_a_general_failure()
    {
        // Exit code 3 tells a caller that retrying is pointless until something changes.
        var exception = Assert.Throws<CliException>(() => _resolver.Resolve(null, null));

        Assert.Equal(ExitCodes.InvalidInput, exception.ExitCode);
        Assert.Equal(ErrorCodes.ServerNotConfigured, exception.Error.Code);
        Assert.NotNull(exception.Error.Remediation);
    }

    [Fact]
    public void An_unknown_profile_names_the_ones_that_exist()
    {
        _store.Save(new CliConfigFile
        {
            Profiles = { ["prod"] = new CliProfile { Server = "https://prod.example.com" } }
        });

        var exception = Assert.Throws<CliException>(() => _resolver.Resolve(null, "typo"));

        Assert.Equal(ExitCodes.InvalidInput, exception.ExitCode);
        Assert.Contains("prod", exception.Error.Remediation);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com")]
    public void A_server_that_is_not_an_http_url_is_rejected_before_any_request(string value)
    {
        var exception = Assert.Throws<CliException>(() => _resolver.Resolve(value, null));

        Assert.Equal(ExitCodes.InvalidInput, exception.ExitCode);
    }
}

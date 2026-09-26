using GroupSplit.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Seeder.Test.Keycloak;

/// <summary>
/// Where the admin calls go. Deployed, Keycloak serves only below <c>/idp</c>, and a worker
/// that called the root was answered 404 on its first request -- which, because the API waits
/// for the realm worker to finish, would have kept the whole deployment from starting.
/// </summary>
public sealed class KeycloakAdminBaseAddressTests
{
    [Theory]
    [InlineData("", "https+http://keycloak/")]
    [InlineData("/", "https+http://keycloak/")]
    [InlineData("/idp", "https+http://keycloak/idp/")]
    [InlineData("idp/", "https+http://keycloak/idp/")]
    [InlineData("/auth/idp", "https+http://keycloak/auth/idp/")]
    public void The_relative_path_sits_between_the_service_and_the_admin_paths(string relativePath, string expected)
    {
        var options = new KeycloakAdminOptions { RelativePath = relativePath };

        Assert.Equal(new Uri(expected), options.BaseAddressFor("https+http://keycloak"));
    }

    /// <summary>A trailing slash matters: without it, relative paths replace the last segment.</summary>
    [Fact]
    public void Admin_paths_resolve_below_the_relative_path()
    {
        var options = new KeycloakAdminOptions { RelativePath = "/idp" };

        var token = new Uri(options.BaseAddressFor("http://keycloak:8080"), "realms/master/protocol/openid-connect/token");

        Assert.Equal("http://keycloak:8080/idp/realms/master/protocol/openid-connect/token", token.AbsoluteUri);
    }

    [Theory]
    [InlineData(null, "https+http://keycloak/")]
    [InlineData("/idp", "https+http://keycloak/idp/")]
    public void The_registered_client_reads_the_relative_path_from_configuration(string? relativePath, string expected)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Keycloak:RelativePath"] = relativePath
        });

        builder.AddKeycloakAdminClient();

        using var host = builder.Build();
        using var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KeycloakAdminClient));

        Assert.Equal(new Uri(expected), http.BaseAddress);
    }
}

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// Boots only the parts of the web app a sign-in touches -- the authentication
/// wiring and the <c>/auth</c> endpoints -- so a test can read the headers a
/// challenge actually puts on the wire. Blazor, the API forwarder and Keycloak
/// itself stay out of it; the discovery document is supplied directly rather
/// than fetched, which is the only thing the handler needs a real realm for.
/// </summary>
internal sealed class SignInHost(WebApplication app) : IAsyncDisposable
{
    public HttpClient Client { get; } = app.GetTestServer().CreateClient();

    public IServiceProvider Services => app.Services;

    /// <summary>Configures the host the way the deployed app is, without starting it.</summary>
    public static WebApplication Build(string? authority)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });

        if (authority is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Keycloak:Authority"] = authority });
        }

        builder.WebHost.UseTestServer();

        builder.AddGroupSplitAuthentication();
        builder.Services.AddAuthorizationBuilder();

        // Stands in for the realm's discovery document. Replaces the manager rather
        // than setting Options.Configuration: the handler only reads the latter while
        // the framework's own post-configure runs, which is already behind us here.
        builder.Services.PostConfigure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            options => options.ConfigurationManager =
                new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration
                    {
                        Issuer = authority,
                        AuthorizationEndpoint = $"{authority}/protocol/openid-connect/auth",
                        TokenEndpoint = $"{authority}/protocol/openid-connect/token",
                        EndSessionEndpoint = $"{authority}/protocol/openid-connect/logout"
                    }));

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapIdentity();

        return app;
    }

    public static async Task<SignInHost> StartAsync(string authority)
    {
        var app = Build(authority);

        await app.StartAsync();

        return new SignInHost(app);
    }

    /// <summary>
    /// The short-lived cookies the OIDC handler writes on a challenge and reads back
    /// on the callback. Losing either one fails the sign-in.
    /// </summary>
    public static IReadOnlyList<string> HandshakeCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(cookie =>
                    cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)
                    || cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal))
                .ToList()
            : [];

    /// <summary>
    /// Splits off the attributes so an assertion cannot be fooled by the cookie's
    /// own randomly generated value happening to contain the word it looks for.
    /// </summary>
    public static IReadOnlyList<string> AttributesOf(string setCookie) =>
        setCookie.Split(';').Skip(1).Select(attribute => attribute.Trim()).ToList();

    /// <summary>Reads a parameter off the authorize URL the challenge redirects to.</summary>
    public static string AuthorizeParameter(HttpResponseMessage response, string name)
    {
        var location = Assert.IsType<Uri>(response.Headers.Location);

        return QueryHelpers.ParseQuery(location.Query).TryGetValue(name, out var value)
            ? value.ToString()
            : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await app.StopAsync();
        await app.DisposeAsync();
    }
}

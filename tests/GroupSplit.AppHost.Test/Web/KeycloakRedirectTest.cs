using System.Net;
using System.Text.Json;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;

namespace GroupSplit.AppHost.Test.Web;

/// <summary>
/// Where the running realm lets a sign-in be sent, asked of Keycloak itself rather than read
/// out of the file. The realm here may be one a data volume has kept since before the file
/// changed, so these pass only if the realm worker actually reconciled it.
/// </summary>
public class KeycloakRedirectTest(AppHostFixture appHost)
{
    private Uri WebOrigin => new(appHost.Application.GetEndpoint("web", "https").GetLeftPart(UriPartial.Authority));

    private HttpClient Keycloak() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = appHost.Application.GetEndpoint("keycloak", "http")
    };

    /// <summary>
    /// The attack a wildcard allowed: an authorization request built by somebody else, asking
    /// for the code to be delivered to their own site. Keycloak has to refuse it outright
    /// rather than redirect anywhere.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task An_authorization_request_for_a_foreign_redirect_is_refused()
    {
        using var keycloak = Keycloak();

        using var response = await keycloak.GetAsync(
            AuthorizationRequest("https://attacker.example/cb"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>A sign-in from the web app itself still gets its login page.</summary>
    [Fact(Timeout = 120_000)]
    public async Task An_authorization_request_for_the_web_app_callback_is_served()
    {
        using var keycloak = Keycloak();

        using var response = await keycloak.GetAsync(
            AuthorizationRequest(new Uri(WebOrigin, "/signin-oidc").AbsoluteUri), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(Timeout = 120_000)]
    public async Task The_web_client_on_the_running_realm_names_only_its_own_callbacks()
    {
        var ct = TestContext.Current.CancellationToken;
        var clients = await appHost.Keycloak.GetAsync("admin/realms/group-split/clients?clientId=web-app", ct);
        var client = Assert.Single(clients.EnumerateArray());
        var origin = WebOrigin.AbsoluteUri.TrimEnd('/');

        Assert.Equal([$"{origin}/signin-oidc"], Strings(client.GetProperty("redirectUris")));
        Assert.Empty(Strings(client.GetProperty("webOrigins")));
        Assert.Equal(
            $"{origin}/signout-callback-oidc",
            client.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString());
    }

    private static string AuthorizationRequest(string redirectUri) =>
        "realms/group-split/protocol/openid-connect/auth"
        + "?client_id=web-app&response_type=code&scope=openid"
        + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();
}

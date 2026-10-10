using System.Net;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>Covers what the <c>/auth</c> endpoints put on the wire.</summary>
public class IdentityEndpointTest
{
    private const string Authority = "http://keycloak.test/realms/group-split";

    [Fact]
    public async Task Register_is_not_routed_now_that_only_an_admin_creates_users()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        var response = await host.Client.GetAsync("/auth/register", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Login_sends_the_browser_to_keycloaks_authorization_endpoint()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        var response = await host.Client.GetAsync("/auth/login", TestContext.Current.CancellationToken);

        var location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        Assert.Contains("/protocol/openid-connect/auth", location, StringComparison.Ordinal);
        Assert.Equal("code", SignInHost.AuthorizeParameter(response, "response_type"));
        Assert.Equal("S256", SignInHost.AuthorizeParameter(response, "code_challenge_method"));
    }

    /// <summary>
    /// Signing out from one of the app's own pages: a form post carrying its antiforgery
    /// token, answered with the redirect to Keycloak's end-session endpoint and an expired
    /// ticket cookie.
    /// </summary>
    [Fact]
    public async Task Signing_out_is_a_post_from_the_apps_own_page()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        using var request = await host.FormPostFromOwnPage("/auth/logout");
        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            $"{Authority}/protocol/openid-connect/logout",
            Assert.IsType<Uri>(response.Headers.Location).ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("GroupSplit.Auth=;", StringComparison.Ordinal));
    }

    /// <summary>
    /// As a GET, any page anywhere could sign a person out with an image tag.
    /// </summary>
    [Fact]
    public async Task Signing_out_with_a_get_is_not_allowed()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        using var response = await host.Client.GetAsync("/auth/logout", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>
    /// A post without the token is one another page made: a form on a sibling subdomain is
    /// the same site, so the SameSite=Lax cookie goes with it.
    /// </summary>
    [Fact]
    public async Task Signing_out_with_a_post_that_carries_no_antiforgery_token_is_refused()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        using var response = await host.Client.PostAsync(
            "/auth/logout",
            new FormUrlEncodedContent(new Dictionary<string, string>()),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>
    /// After the account is deleted, signing out is the app's alone. Keycloak deleted the
    /// login and its sessions with it, so a round trip through its end-session endpoint has
    /// nothing to end -- and goes to the one system that may not be answering.
    /// </summary>
    [Fact]
    public async Task Signing_out_a_deleted_account_stays_in_the_app_and_says_why()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        var response = await host.Client.GetAsync("/auth/account-deleted", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        Assert.Equal("/login?reason=account-deleted", location);
        Assert.DoesNotContain("openid-connect/logout", location, StringComparison.Ordinal);

        // The ticket cookie is expired, which is what being signed out of the app is.
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("GroupSplit.Auth=;", StringComparison.Ordinal)
                      && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_authority_fails_the_start_up_rather_than_the_first_sign_in()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SignInHost.Build(authority: null));

        Assert.Contains("Keycloak:Authority", error.Message, StringComparison.Ordinal);
    }
}

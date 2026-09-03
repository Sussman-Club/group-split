namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>Covers what the <c>/auth</c> endpoints put on the wire.</summary>
public class IdentityEndpointTest
{
    private const string Authority = "http://keycloak.test/realms/group-split";

    [Fact]
    public async Task Register_sends_the_browser_to_keycloaks_registration_page()
    {
        await using var host = await SignInHost.StartAsync(Authority);

        var response = await host.Client.GetAsync("/auth/register", TestContext.Current.CancellationToken);

        var location = Assert.IsType<Uri>(response.Headers.Location).ToString();

        // Keycloak has no request parameter for sign-up; it is a sibling endpoint.
        Assert.Contains("/protocol/openid-connect/registrations", location, StringComparison.Ordinal);
        Assert.DoesNotContain("/protocol/openid-connect/auth", location, StringComparison.Ordinal);
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

    [Fact]
    public void A_missing_authority_fails_the_start_up_rather_than_the_first_sign_in()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SignInHost.Build(authority: null));

        Assert.Contains("Keycloak:Authority", error.Message, StringComparison.Ordinal);
    }
}

using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// Where a sign-in lands afterwards. The crafted cases go through the endpoint as the query
/// string a link would carry, because model binding decodes it once before any check sees it:
/// <c>%09</c> arrives as a tab, and a tab is what browsers drop to turn <c>/&#9;/evil.example</c>
/// into <c>//evil.example</c>.
/// </summary>
public class ReturnUrlTest
{
    private const string Authority = "http://keycloak.test/realms/group-split";

    [Theory]
    [InlineData("%2F%09%2Fevil.example")]
    [InlineData("/%09/evil.example")]
    [InlineData("/%0a/evil.example")]
    [InlineData("/%0d%0a/evil.example")]
    [InlineData("/%5Cevil.example")]
    [InlineData("//evil.example")]
    [InlineData("https%3A%2F%2Fevil.example%2F")]
    [InlineData("evil.example")]
    // Encoded twice, so it reaches the check still encoded once. Nothing on the way to the
    // Location header decodes it again today, but nothing has to start to for this to hold.
    [InlineData("/%255Cevil.example")]
    [InlineData("/%252F/evil.example")]
    [InlineData("/%2509/evil.example")]
    public async Task A_return_url_that_leaves_the_site_lands_on_the_home_page(string query)
    {
        Assert.Equal("/", await LandingAfterSignInAsync(query));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/groups", "/groups")]
    [InlineData("%2Fgroups%3Ftab%3Dledger", "/groups?tab=ledger")]
    [InlineData("/join/abc123", "/join/abc123")]
    public async Task A_return_url_on_the_site_is_kept(string query, string expected)
    {
        Assert.Equal(expected, await LandingAfterSignInAsync(query));
    }

    /// <summary>
    /// The decoded forms, as the endpoint sees them once bound. A backslash cannot travel in
    /// a test request at all: the client rewrites it to a slash before it is sent.
    /// </summary>
    [Theory]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r\n/evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("")]
    [InlineData(null)]
    public void The_bound_value_is_checked_the_same_way(string? returnUrl)
    {
        Assert.Equal("/", IdentityApi.ResolveReturnUrl(returnUrl));
    }

    /// <summary>
    /// The pages hand the value along before the server ever sees it. They must refuse the
    /// same values, or a crafted link survives into a URL the app itself wrote.
    /// </summary>
    [Theory]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/%5Cevil.example")]
    [InlineData("/%09/evil.example")]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example/")]
    public void The_pages_do_not_pass_along_what_the_server_would_refuse(string returnUrl)
    {
        Assert.Equal("/auth/login", AuthRoutes.Login(returnUrl));
        Assert.Equal(
            "/login?reason=session-expired",
            AuthRoutes.Handoff(HandoffReason.SessionExpired, returnUrl));
    }

    [Fact]
    public void The_pages_pass_along_a_path_on_the_site()
    {
        Assert.Equal("/auth/login?returnUrl=%2Fgroups%3Ftab%3Dledger", AuthRoutes.Login("/groups?tab=ledger"));
    }

    /// <summary>
    /// Reads the landing back out of the challenge's own state parameter -- the value the
    /// callback will redirect to, rather than what the code meant to put there.
    /// </summary>
    private static async Task<string?> LandingAfterSignInAsync(string returnUrlQuery)
    {
        await using var host = await SignInHost.StartAsync(Authority);

        using var response = await host.Client.GetAsync(
            $"/auth/login?returnUrl={returnUrlQuery}", TestContext.Current.CancellationToken);

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(SignInHost.AuthorizeParameter(response, "state"));

        return Assert.IsType<AuthenticationProperties>(properties).RedirectUri;
    }
}

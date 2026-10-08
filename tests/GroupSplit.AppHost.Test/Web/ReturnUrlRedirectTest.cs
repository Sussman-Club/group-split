using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;

namespace GroupSplit.AppHost.Test.Web;

// See HomePageTest for why the analyzer is silenced: Playwright takes no CancellationToken.
#pragma warning disable xUnit1069

/// <summary>
/// Where a real sign-in lands when the link that started it carried a crafted return URL:
/// the whole round trip, through Keycloak and back through the OIDC callback, in a browser
/// that strips tabs and newlines out of a URL the way #311 describes.
/// </summary>
/// <remarks>
/// The unit tests read the landing out of the challenge's state. Only this one shows what
/// the browser does with the <c>Location</c> header the callback finally writes.
/// </remarks>
public class ReturnUrlRedirectTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    private readonly AppHostFixture _appHost = appHost;

    private const float OperationTimeoutMs = 60_000;

    /// <summary>
    /// Every page the browser asked for. A browser sent off the site lands on an error page
    /// with nothing on it, so this is where the attempt shows up.
    /// </summary>
    private readonly List<string> _navigations = [];

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        Page.Request += (_, request) =>
        {
            if (request.IsNavigationRequest && request.Frame == Page.MainFrame)
            {
                lock (_navigations) _navigations.Add(request.Url);
            }
        };
    }

    [Theory(Timeout = 240_000)]
    [InlineData("%2F%09%2Fevil.example")]
    [InlineData("%2F%0A%2Fevil.example")]
    [InlineData("%2F%5Cevil.example")]
    [InlineData("%2F%255Cevil.example")]
    public async Task A_crafted_return_url_lands_on_the_home_page(string returnUrl)
    {
        await Page.GotoAsync(Url($"auth/login?returnUrl={returnUrl}"), new PageGotoOptions { Timeout = OperationTimeoutMs });

        await SignInAtKeycloakAsync();

        await AssertLandedOnAsync("/");
    }

    /// <summary>
    /// The way a real link arrives: at the app's own sign-in page, which hands the value on
    /// to the challenge itself.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task A_crafted_return_url_through_the_sign_in_page_lands_on_the_home_page()
    {
        await Page.GotoAsync(Url("login?returnUrl=%2F%09%2Fevil.example"), new PageGotoOptions { Timeout = OperationTimeoutMs });
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continue to sign in" })
            .ClickAsync(new LocatorClickOptions { Timeout = OperationTimeoutMs });

        await SignInAtKeycloakAsync();

        await AssertLandedOnAsync("/");
    }

    /// <summary>
    /// Without this the two above would pass just as well if return URLs were ignored
    /// altogether.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task A_return_url_on_the_site_is_where_the_sign_in_lands()
    {
        await Page.GotoAsync(Url("auth/login?returnUrl=%2Fgroups"), new PageGotoOptions { Timeout = OperationTimeoutMs });

        await SignInAtKeycloakAsync();

        await AssertLandedOnAsync("/groups");
    }

    private async Task SignInAtKeycloakAsync()
    {
        var email = await _appHost.Keycloak.CreateAccountAsync(TestContext.Current.CancellationToken);

        await Page.FillAsync("#username", email, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.FillAsync("#password", KeycloakAdmin.AccountPassword, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.ClickAsync("#kc-login", new PageClickOptions { Timeout = OperationTimeoutMs });
    }

    /// <summary>
    /// Signed in, on the app's own origin, at <paramref name="path"/>. The signed-in nav only
    /// renders once the API has returned a user record, so it also proves the callback was
    /// accepted rather than the browser merely ending up somewhere on the right host.
    /// </summary>
    private async Task AssertLandedOnAsync(string path)
    {
        var keycloak = _appHost.Application.GetEndpoint("keycloak", "http");
        var web = _appHost.Application.GetEndpoint("web");

        // Keycloak answers the sign-in with a page that posts the code back to the app, so
        // the browser is still on Keycloak for a moment after the click. Off it, and settled,
        // the callback's redirect has been followed wherever it pointed.
        try
        {
            await Page.WaitForURLAsync(
                url => !url.StartsWith(keycloak.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal),
                new PageWaitForURLOptions { Timeout = OperationTimeoutMs, WaitUntil = WaitUntilState.Load });
        }
        catch (PlaywrightException exception) when (exception.Message.Contains("net::ERR_", StringComparison.Ordinal))
        {
            // A redirect to a host that does not exist fails to load at all. Where it went is
            // in the navigations, which the assertion below reports.
        }

        lock (_navigations)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Landed on {Page.Url} after: {string.Join(" -> ", _navigations)}");

            Assert.All(_navigations, url =>
            {
                var authority = new Uri(url).Authority;

                Assert.True(authority == web.Authority || authority == keycloak.Authority, $"The browser was sent to {url}.");
            });
        }

        var landed = new Uri(Page.Url);

        Assert.Equal(web.Authority, landed.Authority);
        Assert.Equal(path, landed.AbsolutePath);

        await Expect(Page
                .GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Groups", Exact = true })
                .Filter(new LocatorFilterOptions { Visible = true })
                .First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = OperationTimeoutMs });
    }

    private string Url(string path) =>
        new Uri(_appHost.Application.GetEndpoint("web"), path).AbsoluteUri;
}

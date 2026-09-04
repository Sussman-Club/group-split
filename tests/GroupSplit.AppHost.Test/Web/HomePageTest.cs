using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;

namespace GroupSplit.AppHost.Test.Web;

// xUnit1069: the analyzer wants a test carrying a Timeout to reference
// TestContext.Current.CancellationToken, so the timeout can abort the work rather than
// only fail the test afterwards. Playwright's API takes no CancellationToken anywhere,
// so there is nothing here to hand it to. The Timeout is kept as the outer backstop and
// each call below carries a Playwright timeout of its own, which is what actually stops
// a stalled operation; the two together are the best this API allows.
#pragma warning disable xUnit1069

public class HomePageTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    private readonly AppHostFixture _appHost = appHost;

    /// <summary>
    /// Bounds every Playwright call in this class. Well inside the <c>Timeout</c> on each
    /// test, so a stall surfaces as a Playwright error naming the operation and the
    /// selector, rather than as a bare xUnit timeout that says only which test was
    /// running.
    /// </summary>
    private const float OperationTimeoutMs = 60_000;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        await Page.GotoAsync(
            _appHost.Application.GetEndpoint("web").AbsoluteUri,
            new PageGotoOptions { Timeout = OperationTimeoutMs });
    }

    /// <summary>
    /// The whole stack in one assertion: the browser reached the web app, it rendered,
    /// and the title came from <c>Home.razor</c> rather than from an error page.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_home_page_renders_with_its_own_title()
    {
        await Expect(Page).ToHaveTitleAsync(
            "Group Split",
            new PageAssertionsToHaveTitleOptions { Timeout = OperationTimeoutMs });
    }

    /// <summary>
    /// An anonymous visitor gets the shell and an invitation to sign in, not a redirect
    /// into Keycloak: the home page is the one place reachable without an account.
    /// Scoped to the header because the page also offers sign-in in its body, and an
    /// unscoped locator matches both.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task An_anonymous_visitor_is_offered_a_sign_in()
    {
        var signIn = Page
            .GetByRole(AriaRole.Banner)
            .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Sign in" });

        await Expect(signIn).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = OperationTimeoutMs });
        await Expect(signIn).ToHaveAttributeAsync(
            "href", "/login",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = OperationTimeoutMs });
    }
}

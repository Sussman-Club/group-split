using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;

namespace GroupSplit.AppHost.Test.Web;

public class HomePageTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Page.GotoAsync(appHost.Application.GetEndpoint("web").AbsoluteUri);
    }

    /// <summary>
    /// The whole stack in one assertion: the browser reached the web app, it rendered,
    /// and the title came from <c>Home.razor</c> rather than from an error page.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task The_home_page_renders_with_its_own_title()
    {
        await Expect(Page).ToHaveTitleAsync("Group Split");
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
            .GetByRole(AriaRole.Link, new() { Name = "Sign in" });

        await Expect(signIn).ToBeVisibleAsync();
        await Expect(signIn).ToHaveAttributeAsync("href", "/login");
    }
}

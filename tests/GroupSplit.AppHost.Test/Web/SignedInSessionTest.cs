using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright;

namespace GroupSplit.AppHost.Test.Web;

// See HomePageTest for why the analyzer is silenced: Playwright takes no CancellationToken.
#pragma warning disable xUnit1069

/// <summary>
/// A real sign-in, and what the session does afterwards.
/// </summary>
/// <remarks>
/// The gap these fill: nothing else here signs in, so every part of the token path was
/// covered only by unit tests that stub the authority. Between them these three assert the
/// chain end to end -- the sign-in seeds the token store, the API accepts what the store
/// hands out, an expired access token is replaced without the person doing anything, and
/// signing out ends the session at Keycloak rather than leaving one it will silently hand
/// straight back.
/// <para>
/// They run in the render mode a deployment runs, which the fixture sets: the token used to
/// freeze at page load inside an interactive circuit, and from the WebAssembly mode
/// development uses that is invisible.
/// </para>
/// </remarks>
public class SignedInSessionTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    private readonly AppHostFixture _appHost = appHost;

    private const float OperationTimeoutMs = 60_000;

    /// <summary>
    /// Long enough that the access token the sign-in produced is certainly dead, so a call
    /// that works afterwards can only have been given a refreshed one.
    /// </summary>
    private static readonly TimeSpan PastTokenExpiry =
        TimeSpan.FromSeconds(KeycloakAdmin.AccessTokenLifespanSeconds + 15);

    /// <summary>
    /// The whole authenticated path in one assertion. A sign-in that stored no tokens, a
    /// store the API client cannot read, or a token the API rejects all land here as the
    /// page never reaching its own empty state.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task A_signed_in_person_gets_an_answer_from_the_api()
    {
        await SignInAsync();

        await Expect(Groups).ToBeVisibleAsync(Visible);
    }

    /// <summary>
    /// The bug this suite was missing. Inside an interactive circuit there is no request to
    /// hang a refresh on, and the ambient <c>HttpContext</c> belongs to the connection the
    /// circuit was opened over -- so the token was fixed at page load, and a page open
    /// longer than its lifetime got a 401 from everything it touched.
    /// <para>
    /// Navigating by clicking rather than reloading is the whole point: a reload builds a
    /// new circuit with a fresh token and would pass however broken the refresh is.
    /// </para>
    /// <para>
    /// What it proves is that the path runs and the API answers. It cannot see which token
    /// was sent, so a refresh that somehow returned the same working token would pass too
    /// -- but every way this has actually broken (no store, an unreachable store, a
    /// principal the accessor cannot find, a refresh that is never persisted) fails it.
    /// </para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task A_page_left_open_past_the_token_expiry_still_reaches_the_api()
    {
        await SignInAsync();
        await Expect(Groups).ToBeVisibleAsync(Visible);

        var failuresBefore = await TokenFailuresAsync();

        await Task.Delay(PastTokenExpiry, TestContext.Current.CancellationToken);

        await NavLink("Expenses").ClickAsync(new LocatorClickOptions { Timeout = OperationTimeoutMs });

        // The grid's own empty state, which it renders only once the API has answered it.
        // Exact, or it also matches the page's h1 and the top bar's h2.
        await Expect(Page.GetByRole(
                AriaRole.Heading, new PageGetByRoleOptions { Name = "No expenses yet", Exact = true }))
            .ToBeVisibleAsync(Visible);

        await Expect(Page).ToHaveURLAsync(
            new Regex("/transactions$"),
            new PageAssertionsToHaveURLOptions { Timeout = OperationTimeoutMs });

        // The assertion that means anything. A page that rendered proves little on its own,
        // because a 401 has ApiErrorPresenter sign in again and the session recovers -- so
        // a broken refresh looks like a working one from out here. This is the handler
        // saying it had no token to send, which is what a broken refresh actually is.
        Assert.Equal(failuresBefore, await TokenFailuresAsync());
    }

    /// <summary>
    /// Signing out has to end the session at Keycloak, not just locally. Without the
    /// <c>id_token_hint</c> the sign-out sends, Keycloak cannot tell which session is
    /// ending: the app's own cookie goes, the realm's does not, and the next visit is
    /// handed the same session straight back without anybody typing anything.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task Signing_out_ends_the_session_at_the_authority()
    {
        await SignInAsync();
        await Expect(Groups).ToBeVisibleAsync(Visible);

        await SignOutAsync();

        await Page.GotoAsync(
            Url("auth/login?returnUrl=/groups"), new PageGotoOptions { Timeout = OperationTimeoutMs });

        // A realm session that survived the sign-out would send the browser straight back
        // to /groups instead of asking again.
        await Expect(Page.Locator("#password")).ToBeVisibleAsync(Visible);
    }

    /// <summary>
    /// The empty state of the page the sign-in lands on. Specific to the page having been
    /// told, by the API, that there are no groups -- which a fresh account is true of.
    /// </summary>
    private ILocator Groups => Page.GetByText("No group yet");

    /// <summary>
    /// How many times the API client has reported having no token to send. The marker is
    /// the one <c>AuthDelegatingHandler</c> logs, and it is logged for every reason a token
    /// can be unavailable.
    /// </summary>
    private async Task<int> TokenFailuresAsync()
    {
        const string marker = "No access token for";

        var log = await _appHost.ReadLogAsync("web");

        return (log.Length - log.Replace(marker, string.Empty, StringComparison.Ordinal).Length)
               / marker.Length;
    }

    /// <summary>
    /// Signs out, tolerating the abort Chromium reports for the navigation. The sign-out is
    /// a redirect chain out to the realm and back into a Blazor page, and the document that
    /// started it is superseded on the way -- which arrives here as ERR_ABORTED even though
    /// every hop was followed.
    /// </summary>
    private async Task SignOutAsync()
    {
        try
        {
            await Page.GotoAsync(
                Url("auth/logout"), new PageGotoOptions { Timeout = OperationTimeoutMs });
        }
        catch (PlaywrightException exception) when (exception.Message.Contains("ERR_ABORTED"))
        {
            // Followed anyway; the assertion after this is what says whether it worked.
        }

        await Page.WaitForLoadStateAsync(
            LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = OperationTimeoutMs });
    }

    /// <summary>
    /// The nav item on screen. The menu is rendered twice, once in the sidebar and once as
    /// the mobile tab bar, so an unscoped role lookup matches two and Playwright refuses it.
    /// Filter rather than a chained visible= selector, which descends into the link and
    /// matches its icon and label instead of the link itself. Exact, because a role name is
    /// otherwise a case-insensitive substring: "Expenses" also matches the brand link, whose
    /// accessible name ends "Shared expenses", and that one comes first in the document.
    /// </summary>
    private ILocator NavLink(string name) =>
        Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = name, Exact = true })
            .Filter(new LocatorFilterOptions { Visible = true })
            .First;

    private static LocatorAssertionsToBeVisibleOptions Visible =>
        new() { Timeout = OperationTimeoutMs };

    private string Url(string path) =>
        new Uri(_appHost.Application.GetEndpoint("web"), path).AbsoluteUri;

    /// <summary>
    /// Signs in as an account made for this test, through Keycloak's own form, and waits
    /// until the page it was sent back to has rendered.
    /// </summary>
    private async Task SignInAsync()
    {
        var email = await _appHost.Keycloak.CreateAccountAsync(TestContext.Current.CancellationToken);

        // Straight at the challenge rather than through the app's own sign-in page, which
        // only navigates here anyway.
        await Page.GotoAsync(
            Url("auth/login?returnUrl=/groups"), new PageGotoOptions { Timeout = OperationTimeoutMs });

        await Page.FillAsync("#username", email, new PageFillOptions { Timeout = OperationTimeoutMs });
        await Page.FillAsync(
            "#password", KeycloakAdmin.AccountPassword, new PageFillOptions { Timeout = OperationTimeoutMs });

        await Page.ClickAsync("#kc-login", new PageClickOptions { Timeout = OperationTimeoutMs });

        // The app, not the realm: proof the callback was accepted and a session exists.
        // The signed-in nav only renders once the API has returned a user record.
        await Expect(NavLink("Groups")).ToBeVisibleAsync(Visible);
    }
}

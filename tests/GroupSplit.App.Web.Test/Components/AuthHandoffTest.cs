using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Errors;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The sign-in hand-off. It collects one thing -- whether to stay signed in -- and that is
/// the whole reason it waits for a click instead of redirecting by itself.
/// </summary>
public class AuthHandoffTest : ComponentTest
{
    private readonly Mock<IAuthService> _auth = new();

    public AuthHandoffTest() => Services.AddSingleton(_auth.Object);

    /// <summary>
    /// It used to hand off on first render. A checkbox nobody is given time to see is the
    /// bug this was written against, one layer up.
    /// </summary>
    [Fact]
    public void Nobody_is_sent_to_keycloak_before_they_have_answered()
    {
        Render();

        _auth.Verify(
            service => service.Login(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void The_tick_travels_with_the_sign_in()
    {
        var page = Render();

        page.Find("input[type=checkbox]").Change(true);
        page.Find("button").Click();

        _auth.Verify(service => service.Login(null, true, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void Left_unticked_the_sign_in_says_so_rather_than_saying_nothing()
    {
        Render().Find("button").Click();

        _auth.Verify(service => service.Login(null, false, It.IsAny<CancellationToken>()));
    }

    /// <summary>
    /// The promise has to be a period the page can state, or the checkbox is back to
    /// meaning whatever the reader assumes.
    /// </summary>
    [Fact]
    public void The_page_says_how_long_being_remembered_lasts()
    {
        Assert.Contains(
            $"{(int)AuthenticationExtensions.RememberedSessionLifetime.TotalDays} days",
            Render().Markup,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page load does not come back, so the second click would start a second
    /// hand-off over the top of the first.
    /// </summary>
    [Fact]
    public void A_second_click_does_not_start_a_second_hand_off()
    {
        var page = Render();

        page.Find("button").Click();
        page.Find("button").Click();

        _auth.Verify(
            service => service.Login(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The whole point of coming here rather than straight to the challenge: the person is
    /// told what happened, and the page they were on is still waiting for them afterwards.
    /// The button stays the way to go on -- there is nowhere else to go from here.
    /// </summary>
    [Fact]
    public void A_session_expiry_is_explained_before_the_original_page_is_tried_again()
    {
        GoTo(AuthRoutes.Handoff(HandoffReason.SessionExpired, "/groups"));

        var page = Render();

        Assert.Contains(ErrorMessages.SessionExpired, page.Markup, StringComparison.Ordinal);

        page.Find("button").Click();

        _auth.Verify(service => service.Login("/groups", false, It.IsAny<CancellationToken>()));
    }

    /// <summary>
    /// Where deleting an account lands. It reports something the person did, not something
    /// that went wrong, so it is not shown as an error.
    /// </summary>
    [Fact]
    public void A_deleted_account_is_told_it_is_gone_and_signed_out_without_an_error()
    {
        GoTo(AuthRoutes.Handoff(HandoffReason.AccountDeleted));

        var page = Render();

        Assert.Contains(ErrorMessages.AccountDeletedSignedOut, page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll(".mud-alert-outlined-error"));
        Assert.NotEmpty(page.FindAll(".mud-alert-outlined-info"));
    }

    /// <summary>
    /// The URL says which of our sentences applies, never what the sentence is. A page that
    /// rendered the query string would let anyone put their own words -- "your account is
    /// locked, call this number" -- in a red box on the app's real sign-in page.
    /// </summary>
    [Theory]
    [InlineData("Your account is locked. Call +34 600 000 000.")]
    [InlineData("session-expired-in-some-later-version")]
    public void Nothing_but_this_apps_own_wording_reaches_the_page(string reason)
    {
        GoTo($"/login?reason={Uri.EscapeDataString(reason)}");

        var page = Render();

        Assert.DoesNotContain(reason, page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorMessages.SessionExpired, page.Markup, StringComparison.Ordinal);
    }

    private void GoTo(string url) =>
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

    private IRenderedComponent<AuthHandoff> Render() =>
        Render<AuthHandoff>(parameters => parameters
            .Add(handoff => handoff.Title, "Welcome back")
            .Add(handoff => handoff.Subtitle, "Pick up where you left them.")
            .Add(handoff => handoff.ActionLabel, "Continue to sign in"));
}

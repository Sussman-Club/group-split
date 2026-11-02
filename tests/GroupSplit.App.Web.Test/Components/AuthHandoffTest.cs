using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Errors;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// Web sign-in and expiry recovery start directly; invitations and deletion notices remain explicit.
/// </summary>
public class AuthHandoffTest : ComponentTest
{
    private readonly Mock<IAuthService> _auth = new();

    public AuthHandoffTest() => Services.AddSingleton(_auth.Object);

    [Theory]
    [InlineData("/join/invitation-token")]
    [InlineData("/claim/invitation-token")]
    public void Cancelled_invitation_sign_in_keeps_the_destination_for_retry(string destination)
    {
        _auth.Setup(service => service.Login(destination, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var page = Render<AuthHandoff>(parameters => parameters
            .Add(value => value.Title, "Join a group")
            .Add(value => value.Subtitle, "Sign in to continue")
            .Add(value => value.ActionLabel, "Continue")
            .Add(value => value.ReturnUrl, destination));
        page.Find("button").Click();
        page.WaitForAssertion(() => Assert.Contains("Sign-in was cancelled", page.Markup));
        page.Find("button").Click();
        _auth.Verify(service => service.Login(destination, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void Failed_native_sign_in_shows_a_retryable_message()
    {
        _auth.Setup(service => service.Login(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("private server details"));
        var page = Render();
        page.Find("button").Click();
        Assert.Contains("Check your connection and try again", page.Markup);
        Assert.DoesNotContain("private server details", page.Markup);
        Assert.False(page.Find("button").HasAttribute("disabled"));
        page.Find("button").Click();
        _auth.Verify(service => service.Login(null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// Invitation handoffs wait for the person to choose to continue.
    /// </summary>
    [Fact]
    public void An_invitation_does_not_start_sign_in_before_continue()
    {
        Render();

        _auth.Verify(
            service => service.Login(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Native_login_has_no_persistence_checkbox_and_still_waits_for_a_click()
    {
        Services.AddSingleton(new AppLinks(new Uri("https://groupsplit.example")));
        var page = Render<AuthHandoff>(parameters => parameters.Add(value => value.AutoStart, true));
        Assert.Empty(page.FindAll("input[type=checkbox]"));
        _auth.Verify(service => service.Login(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        page.Find("button").Click();
        _auth.Verify(service => service.Login(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Ordinary_web_login_starts_without_a_second_form()
    {
        var page = Render<AuthHandoff>(parameters => parameters
            .Add(value => value.AutoStart, true));

        _auth.Verify(service => service.Login(null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(page.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void Session_expiry_recovers_automatically_and_preserves_the_destination()
    {
        GoTo(AuthRoutes.Handoff(HandoffReason.SessionExpired, "/groups"));
        var page = Render<AuthHandoff>(parameters => parameters.Add(value => value.AutoStart, true));

        Assert.DoesNotContain(ErrorMessages.SessionExpired, page.Markup);
        _auth.Verify(service => service.Login("/groups", It.IsAny<CancellationToken>()), Times.Once);
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
            service => service.Login(It.IsAny<string?>(), It.IsAny<CancellationToken>()),
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

        _auth.Verify(service => service.Login("/groups", It.IsAny<CancellationToken>()));
    }

    /// <summary>
    /// Where deleting an account lands. It reports something the person did, not something
    /// that went wrong, so it is not shown as an error.
    /// </summary>
    [Fact]
    public void A_deleted_account_is_told_it_is_gone_and_signed_out_without_an_error()
    {
        GoTo(AuthRoutes.Handoff(HandoffReason.AccountDeleted));

        var page = Render<AuthHandoff>(parameters => parameters.Add(value => value.AutoStart, true));

        _auth.Verify(service => service.Login(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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

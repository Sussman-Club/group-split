using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services;
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
    /// The error is what the identity provider sent back. The button stays the way to try
    /// again -- there is nowhere else to go from here.
    /// </summary>
    [Fact]
    public void A_failed_attempt_can_be_tried_again()
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/login?error=Something%20went%20wrong");

        var page = Render();

        Assert.Contains("Something went wrong", page.Markup, StringComparison.Ordinal);

        page.Find("button").Click();

        _auth.Verify(service => service.Login(null, false, It.IsAny<CancellationToken>()));
    }

    private IRenderedComponent<AuthHandoff> Render() =>
        Render<AuthHandoff>(parameters => parameters
            .Add(handoff => handoff.Title, "Welcome back")
            .Add(handoff => handoff.Subtitle, "Pick up where you left them.")
            .Add(handoff => handoff.ActionLabel, "Continue to sign in"));
}

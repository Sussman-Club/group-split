using System.Text.Json;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Authentication;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// What "Keep me signed in" has to be worth. The tick is collected on the app's own
/// sign-in page, carried across the round trip to Keycloak in the challenge properties,
/// and spent here -- on a cookie the browser keeps and an expiry the realm agrees with.
/// </summary>
public class RememberMeTest
{
    private static readonly DateTimeOffset SignedInAt = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// No <c>IsPersistent</c> is what makes the browser drop the cookie when it closes,
    /// and no <c>ExpiresUtc</c> leaves the cookie handler to apply its own thirty minutes.
    /// Both have to stay unset rather than be set to something short.
    /// </summary>
    [Fact]
    public void Without_the_tick_the_session_ends_with_the_browser()
    {
        var properties = IdentityApi.ChallengeProperties("/groups", remember: false);

        AuthenticationExtensions.RememberSession(properties, Time());

        Assert.False(properties.IsPersistent);
        Assert.Null(properties.ExpiresUtc);
        Assert.Null(properties.AllowRefresh);
    }

    [Fact]
    public void The_tick_writes_a_cookie_the_browser_keeps()
    {
        var properties = IdentityApi.ChallengeProperties("/groups", remember: true);

        AuthenticationExtensions.RememberSession(properties, Time());

        Assert.True(properties.IsPersistent);
        Assert.Equal(SignedInAt.Add(AuthenticationExtensions.RememberedSessionLifetime), properties.ExpiresUtc);
    }

    /// <summary>
    /// Sliding would push the ticket past the Keycloak session it is refreshed against,
    /// which ends thirty days after the sign-in whatever the cookie says. From inside the
    /// app that reads as being signed out at a moment nothing announced.
    /// </summary>
    [Fact]
    public void A_remembered_session_is_thirty_days_from_the_sign_in_and_not_from_the_last_visit()
    {
        var properties = IdentityApi.ChallengeProperties(returnUrl: null, remember: true);

        AuthenticationExtensions.RememberSession(properties, Time());

        Assert.False(properties.AllowRefresh);
    }

    /// <summary>
    /// The tick still has to leave the sign-in landing where it was going.
    /// </summary>
    [Fact]
    public void Remembering_does_not_disturb_the_return_url()
    {
        Assert.Equal("/groups", IdentityApi.ChallengeProperties("/groups", remember: true).RedirectUri);
        Assert.Equal("/", IdentityApi.ChallengeProperties("//evil.example", remember: true).RedirectUri);
    }

    /// <summary>
    /// The layer that actually ends a remembered session first is whichever of the two has
    /// the shorter opinion, so they are not allowed to hold different ones: Keycloak stops
    /// refreshing at <c>ssoSessionMaxLifespan</c>, or sooner at <c>ssoSessionIdleTimeout</c>,
    /// and the cookie stops at <see cref="AuthenticationExtensions.RememberedSessionLifetime"/>.
    /// Either one moving alone brings the reported symptom back in a quieter form.
    /// </summary>
    [Theory]
    [InlineData("ssoSessionIdleTimeout")]
    [InlineData("ssoSessionMaxLifespan")]
    public void The_realm_ends_a_remembered_session_at_the_same_moment_the_cookie_does(string setting)
    {
        using var realm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Authentication", "realms.json")));

        Assert.Equal(
            (int)AuthenticationExtensions.RememberedSessionLifetime.TotalSeconds,
            realm.RootElement.GetProperty(setting).GetInt32());
    }

    /// <summary>
    /// Keycloak has a "Remember Me" of its own and keeps the answer to itself, so an app
    /// federated to it cannot find out which was chosen. Leaving it on would put a second
    /// checkbox in front of people that changes nothing this side can see.
    /// </summary>
    [Fact]
    public void The_realm_does_not_ask_the_question_a_second_time()
    {
        using var realm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Authentication", "realms.json")));

        Assert.False(realm.RootElement.GetProperty("rememberMe").GetBoolean());
    }

    /// <summary>
    /// The sign-in screens print a number of days. It is copy, and copy that disagrees with
    /// the thing it describes is the same defect as a checkbox that does nothing.
    /// </summary>
    [Fact]
    public void The_sign_in_screens_promise_the_period_the_cookie_actually_gets()
    {
        Assert.Equal(
            (int)AuthenticationExtensions.RememberedSessionLifetime.TotalDays,
            RememberMeChoice.Days);
    }

    private static TimeProvider Time() => new FixedTime(SignedInAt);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

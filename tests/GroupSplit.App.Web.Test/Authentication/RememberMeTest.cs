using System.Text.Json;
using GroupSplit.App.Shared.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

public class RememberMeTest
{
    [Fact]
    public void The_app_cookie_stays_temporary_and_uses_a_short_sliding_lifetime()
    {
        using var app = SignInHost.Build("https://keycloak.test/realms/group-split");
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = IdentityApi.ChallengeProperties("/groups");

        Assert.False(properties.IsPersistent);
        Assert.Null(properties.ExpiresUtc);
        Assert.Equal(TimeSpan.FromMinutes(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/groups", properties.RedirectUri);
        Assert.Equal("/", IdentityApi.ChallengeProperties("//evil.example").RedirectUri);
    }

    [Theory]
    [InlineData("ssoSessionIdleTimeoutRememberMe")]
    [InlineData("ssoSessionMaxLifespanRememberMe")]
    public void Keycloak_owns_the_thirty_day_remembered_session(string setting)
    {
        using var realm = Realm();
        Assert.True(realm.RootElement.GetProperty("rememberMe").GetBoolean());
        Assert.Equal((int)AuthenticationExtensions.RememberedSessionLifetime.TotalSeconds,
            realm.RootElement.GetProperty(setting).GetInt32());
        Assert.Equal(RememberMeChoice.Days, (int)AuthenticationExtensions.RememberedSessionLifetime.TotalDays);
    }

    [Fact]
    public void Ordinary_sso_sessions_are_shorter_than_remembered_sessions()
    {
        using var realm = Realm();
        Assert.Equal(1800, realm.RootElement.GetProperty("ssoSessionIdleTimeout").GetInt32());
        Assert.Equal(43200, realm.RootElement.GetProperty("ssoSessionMaxLifespan").GetInt32());
    }

    private static JsonDocument Realm() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Authentication", "realms.json")));
}

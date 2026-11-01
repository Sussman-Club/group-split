using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Moq;
using Duende.AccessTokenManagement.OpenIdConnect;

namespace GroupSplit.App.Web.Test.Authentication;

public class RememberMeTest
{
    [Fact]
    public void Every_provider_gets_a_persistent_fifteen_day_sliding_ticket()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var properties = IdentityApi.ChallengeProperties("/groups");
        AuthenticationExtensions.PersistSession(properties, new FixedTime(now));
        Assert.True(properties.IsPersistent);
        Assert.True(properties.AllowRefresh);
        Assert.Equal(now.AddDays(15), properties.ExpiresUtc);
        Assert.Equal(now.AddDays(90).ToString("O"), properties.Items[AuthenticationExtensions.SessionDeadlineItem]);
        Assert.Equal("/groups", properties.RedirectUri);
        Assert.Equal("/", IdentityApi.ChallengeProperties("//evil.example").RedirectUri);
    }

    [Fact]
    public void The_cookie_handler_allows_sliding_with_the_common_inactivity_window()
    {
        using var app = SignInHost.Build("https://keycloak.test/realms/group-split");
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal(TimeSpan.FromDays(15), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.True(options.Cookie.HttpOnly);
    }

    [Theory]
    [InlineData("ssoSessionIdleTimeout", 15)]
    [InlineData("ssoSessionMaxLifespan", 90)]
    public void All_keycloak_sessions_support_the_same_bounded_lifetime(string setting, int days)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Authentication", "realms.json")));
        Assert.False(realm.RootElement.GetProperty("rememberMe").GetBoolean());
        Assert.Equal((int)TimeSpan.FromDays(days).TotalSeconds,
            realm.RootElement.GetProperty(setting).GetInt32());
    }

    [Theory]
    [InlineData(5, 20)]
    [InlineData(14, 29)]
    [InlineData(85, 90)]
    public async Task Activity_moves_the_idle_deadline_without_extending_the_absolute_limit(int day, int expiresOnDay)
    {
        var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var properties = new AuthenticationProperties();
        AuthenticationExtensions.PersistSession(properties, new FixedTime(start));
        var context = Validation(properties, start.AddDays(day));

        await AuthenticationExtensions.RenewSession(context);

        Assert.True(context.ShouldRenew);
        Assert.Equal(start.AddDays(expiresOnDay), properties.ExpiresUtc);
        Assert.Equal(start.AddDays(90).ToString("O"), properties.Items[AuthenticationExtensions.SessionDeadlineItem]);
    }

    [Fact]
    public async Task Background_connection_requests_do_not_extend_the_session()
    {
        var start = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties();
        AuthenticationExtensions.PersistSession(properties, new FixedTime(start));
        var context = Validation(properties, start.AddDays(5));
        context.Request.Path = "/_blazor";
        var before = properties.ExpiresUtc;
        await AuthenticationExtensions.RenewSession(context);
        Assert.False(context.ShouldRenew);
        Assert.Equal(before, properties.ExpiresUtc);
    }

    [Fact]
    public async Task The_absolute_deadline_rejects_the_ticket_and_discards_its_tokens()
    {
        var start = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties();
        AuthenticationExtensions.PersistSession(properties, new FixedTime(start));
        var context = Validation(properties, start.AddDays(90));
        var principal = context.Principal!;
        var tokens = new Mock<IUserTokenStore>();
        var authentication = new Mock<IAuthenticationService>();
        using var services = new ServiceCollection().AddSingleton(tokens.Object)
            .AddSingleton(authentication.Object).BuildServiceProvider();
        context.HttpContext.RequestServices = services;

        await AuthenticationExtensions.RenewSession(context);

        Assert.Null(context.Principal);
        tokens.Verify(store => store.ClearTokenAsync(principal, null, It.IsAny<CancellationToken>()), Times.Once);
        authentication.Verify(service => service.SignOutAsync(context.HttpContext,
            CookieAuthenticationDefaults.AuthenticationScheme, null), Times.Once);
    }

    private static CookieValidatePrincipalContext Validation(AuthenticationProperties properties, DateTimeOffset now)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/groups";
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme,
            null, typeof(CookieAuthenticationHandler));
        return new CookieValidatePrincipalContext(http, scheme,
            new CookieAuthenticationOptions { TimeProvider = new FixedTime(now) },
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")), properties, scheme.Name));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

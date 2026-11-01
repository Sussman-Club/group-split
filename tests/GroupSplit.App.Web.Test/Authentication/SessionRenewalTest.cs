using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

public class SessionRenewalTest
{
    [Fact]
    public async Task Activity_before_half_the_window_still_extends_the_cookie_and_server_ticket()
    {
        var (host, clock, cookie) = await Start();
        await using var owned = host;
        clock.Now = clock.Start.AddDays(5);
        using var visit = await Get(host, "/test/visit", cookie);
        Assert.Equal(HttpStatusCode.OK, visit.StatusCode);
        Assert.Contains(visit.Headers.GetValues("Set-Cookie"), value => value.StartsWith("GroupSplit.Auth="));
        clock.Now = clock.Start.AddDays(19);
        using var later = await Get(host, "/api/test-read", cookie);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task Fifteen_days_without_activity_expires_the_session_on_the_server()
    {
        var (host, clock, cookie) = await Start();
        await using var owned = host;
        clock.Now = clock.Start.AddDays(15).AddMinutes(1);
        using var response = await Get(host, "/api/test-read", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_activity_never_extends_a_session_past_ninety_days()
    {
        var (host, clock, cookie) = await Start();
        await using var owned = host;
        for (var day = 14; day < 90; day += 14)
        {
            clock.Now = clock.Start.AddDays(day);
            using var visit = await Get(host, "/test/visit", cookie);
            Assert.Equal(HttpStatusCode.OK, visit.StatusCode);
        }
        clock.Now = clock.Start.AddDays(90).AddMinutes(1);
        using var response = await Get(host, "/api/test-read", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<(SignInHost Host, Clock Clock, string Cookie)> Start()
    {
        var clock = new Clock();
        var app = SignInHost.Build("http://keycloak.test/realms/group-split");
        app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TimeProvider = clock;
        app.MapGet("/test/sign-in", async (HttpContext context) =>
        {
            var properties = new AuthenticationProperties();
            AuthenticationExtensions.PersistSession(properties, clock);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "renewal-test")], "test")), properties);
        });
        app.MapGet("/test/visit", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/api/test-read", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var host = new SignInHost(app);
        using var signedIn = await host.Client.GetAsync("/test/sign-in", TestContext.Current.CancellationToken);
        var cookie = signedIn.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("GroupSplit.Auth=")).Split(';')[0];
        return (host, clock, cookie);
    }

    private static async Task<HttpResponseMessage> Get(SignInHost host, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Start { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

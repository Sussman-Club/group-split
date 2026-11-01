using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace GroupSplit.AppHost.Test.Web;

#pragma warning disable xUnit1069

public class SessionActivityTest : PageTest
{
    [Fact(Timeout = 120_000)]
    public async Task Only_user_activity_touches_the_session_and_carries_the_antiforgery_field()
    {
        var requests = 0;
        string? body = null;
        await Page.Clock.InstallAsync(new() { TimeDate = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc) });
        await Page.RouteAsync("https://session.test/**", async route =>
        {
            if (route.Request.Method == "POST")
            {
                body = route.Request.PostData;
                Interlocked.Increment(ref requests);
                await route.FulfillAsync(new() { Status = 204 });
            }
            else
            {
                await route.FulfillAsync(new() { ContentType = "text/html", Body = """
                    <form id="gs-session-activity" action="/auth/session" method="post" hidden>
                      <input name="__RequestVerificationToken" value="test-antiforgery-value">
                    </form>
                    <button>Use app</button>
                    """ });
            }
        });
        await Page.GotoAsync("https://session.test/");
        await Page.AddScriptTagAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "session-activity.js") });
        await Page.Clock.FastForwardAsync((long)TimeSpan.FromDays(16).TotalMilliseconds);
        Assert.Equal(0, requests);
        var first = await Page.RunAndWaitForResponseAsync(
            () => Page.GetByRole(AriaRole.Button, new() { Name = "Use app" }).ClickAsync(),
            response => response.Url.EndsWith("/auth/session") && response.Status == 204);
        await first.FinishedAsync();
        Assert.Equal(1, requests);
        Assert.Contains("test-antiforgery-value", body);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Use app" }).ClickAsync();
        Assert.Equal(1, requests);
        await Page.Clock.FastForwardAsync(6 * 60 * 1000);
        Assert.Equal(1, requests);
        var second = await Page.RunAndWaitForResponseAsync(
            () => Page.GetByRole(AriaRole.Button, new() { Name = "Use app" }).ClickAsync(),
            response => response.Url.EndsWith("/auth/session") && response.Status == 204);
        await second.FinishedAsync();
        Assert.Equal(2, requests);
    }
}

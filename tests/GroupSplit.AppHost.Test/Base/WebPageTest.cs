using Microsoft.Playwright.Xunit.v3;

namespace GroupSplit.AppHost.Test.Base
{
    public class WebPageTest(AppHostFixture appHost) : PageTest
    {
        public override async ValueTask InitializeAsync()
        {
            await appHost.WaitForAsync(
                "web",
                (notifications, token) => notifications.WaitForResourceHealthyAsync("web", token),
                "become healthy");

            await base.InitializeAsync();
        }
    }
}

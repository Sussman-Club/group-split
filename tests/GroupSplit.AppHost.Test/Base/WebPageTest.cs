using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace GroupSplit.AppHost.Test.Base
{
    public class WebPageTest(AppHostFixture appHost) : PageTest
    {
        /// <summary>
        /// The web endpoint is HTTPS behind the ASP.NET Core development certificate. That
        /// certificate is a trusted authority on a developer machine and is not one on a
        /// hosted runner, so Chromium refused the endpoint with
        /// <c>ERR_CERT_AUTHORITY_INVALID</c> at <c>GotoAsync</c> — before any assertion ran
        /// — and the CI job was pulled because of it.
        ///
        /// Accepting the certificate here keeps these tests about the app rather than about
        /// the certificate chain, which is the thing they were written to cover. Trusting it
        /// on the runner instead means <c>dotnet dev-certs https --trust</c>, which on Linux
        /// only reaches Chromium when <c>certutil</c> and the right NSS database are present:
        /// more moving parts, all of them outside what the tests are for.
        /// </summary>
        public override BrowserNewContextOptions ContextOptions()
        {
            // Built on the base options rather than replacing them, so any default the
            // Playwright fixture sets (video, trace, viewport) survives.
            var options = base.ContextOptions() ?? new BrowserNewContextOptions();
            options.IgnoreHTTPSErrors = true;
            return options;
        }

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

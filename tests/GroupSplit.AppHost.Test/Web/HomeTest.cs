using Aspire.Hosting.Testing;
using GroupSplit.AppHost;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Playwright.Xunit.v3;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GroupSplit.AppHost.Test.Web;

public class HomePageTest(AppHostFixture appHost) : WebPageTest(appHost)
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Page.GotoAsync(appHost.Application.GetEndpoint("web").AbsoluteUri);
    }

    [Fact]
    public async Task HasTitle()
    {
        await Expect(Page).ToHaveTitleAsync(new Regex("Home"));
    }
}
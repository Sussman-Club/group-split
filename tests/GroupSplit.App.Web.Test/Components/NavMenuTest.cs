using Bunit;
using GroupSplit.App.Shared.Layout;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.App.Web.Test.Components;

public class NavMenuTest : ComponentTest
{
    [Theory]
    [InlineData("/settings")]
    [InlineData("/account")]
    public void Settings_and_legacy_account_routes_select_the_settings_tab(string route)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var page = Render<NavMenu>(parameters => parameters
            .AddCascadingValue(new UserInfo(Guid.NewGuid(), "Omar", "Haddad", "omar@example.com")));

        Assert.Equal(3, page.FindAll("a").Count);
        Assert.Equal("Settings", page.Find("a[href='/settings']").TextContent.Trim());
        Assert.Contains("--gs-pill-index: 2", page.Find("nav").GetAttribute("style"));
    }

    [Fact]
    public void Signed_out_navigation_offers_sign_in()
    {
        var page = Render<NavMenu>();
        Assert.Empty(page.FindAll("a[href='/settings']"));
        Assert.Single(page.FindAll("a[href='/login']"));
    }
}

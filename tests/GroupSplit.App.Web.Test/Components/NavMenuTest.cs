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

        Assert.Equal(4, page.FindAll("a").Count);
        Assert.Equal("Settings", page.Find("a[href='/settings']").TextContent.Trim());
        Assert.Contains("--gs-pill-index: 3", page.Find("nav").GetAttribute("style"));
    }

    [Theory]
    [InlineData("/inbox")]
    [InlineData("/inbox/receipts")]
    [InlineData("/inbox/receipts/00000000-0000-0000-0000-000000000001")]
    public void Both_inbox_views_keep_the_primary_inbox_destination_selected(string route)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        var page = Render<NavMenu>(parameters => parameters
            .AddCascadingValue(new UserInfo(Guid.NewGuid(), "Omar", "Haddad", "omar@example.com")));
        Assert.Single(page.FindAll("a[href='/inbox']"));
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

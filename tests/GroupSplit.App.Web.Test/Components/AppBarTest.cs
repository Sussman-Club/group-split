using Bunit;
using GroupSplit.App.Shared.Layout;
using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

public class AppBarTest : ComponentTest
{
    [Fact]
    public void Signed_in_users_can_open_help_from_the_top_bar()
    {
        Services.AddSingleton<ThemePreference>();
        var bar = Render<AppBar>(parameters => parameters
            .AddCascadingValue(new UserInfo(Guid.NewGuid(), "Daniel", "Rivero", "daniel@test.com")));
        var help = bar.Find("a.gs-topbar-help");
        Assert.Equal("/help", help.GetAttribute("href"));
        Assert.Equal("Help & feedback", help.GetAttribute("aria-label"));
        Assert.Equal("Help", help.QuerySelector(".gs-topbar-help-short")!.TextContent.Trim());
    }

    [Fact]
    public void Signed_out_users_are_not_offered_an_authenticated_help_page()
    {
        Services.AddSingleton<ThemePreference>();
        var bar = Render<AppBar>();
        Assert.Empty(bar.FindAll("a.gs-topbar-help"));
    }

    [Fact]
    public void An_open_account_menu_can_finish_rendering_after_sign_out()
    {
        Services.AddSingleton<ThemePreference>();
        var host = Render<CascadingValue<UserInfo?>>(parameters => parameters
            .Add(cascade => cascade.Value,
                new UserInfo(Guid.NewGuid(), "Daniel", "Rivero", "daniel@test.com"))
            .AddChildContent<AppBar>());
        var bar = host.FindComponent<AppBar>();
        // The popover can render its existing fragment after the session changes.
        var menuContent = bar.FindComponent<MudMenu>().Instance.ChildContent!;

        host.Render(parameters => parameters
            .Add(cascade => cascade.Value, null)
            .AddChildContent<AppBar>());

        var closingMenu = Render(menuContent);
        Assert.Contains("Daniel Rivero", closingMenu.Markup);
        Assert.Contains("Sign in", bar.Markup);
    }
}

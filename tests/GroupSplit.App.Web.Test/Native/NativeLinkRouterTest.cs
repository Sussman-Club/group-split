using GroupSplit.App.Services;
using GroupSplit.App.Shared.Services;

namespace GroupSplit.App.Web.Test.Native;

public sealed class NativeLinkRouterTest
{
    [Fact]
    public void A_notification_group_waits_for_the_webview_and_empty_ids_are_ignored()
    {
        var links = new NativeLinkRouter(NativeOptions.Create("https://api.example/", "https://auth.example/", false));
        links.OpenGroup(Guid.Empty);
        Assert.Null(links.Take());
        var group = Guid.NewGuid();
        links.OpenGroup(group);
        Assert.Equal($"/groups/{group}", links.Take());
    }
    private static readonly Uri Website = new("https://groupsplit.sussman.win/");

    [Theory]
    [InlineData("https://groupsplit.sussman.win/join/token_123", "/join/token_123")]
    [InlineData("https://groupsplit.sussman.win/claim/token-123", "/claim/token-123")]
    [InlineData("groupsplit://invite/join/token", "/join/token")]
    [InlineData("groupsplit://invite/claim/token", "/claim/token")]
    public void Invitations_resolve_to_the_shared_join_and_claim_pages(string link, string route) =>
        Assert.Equal(route, NativeLinkRouter.Route(new Uri(link), Website));

    [Theory]
    [InlineData("https://attacker.example/join/token")]
    [InlineData("https://groupsplit.sussman.win.attacker.example/join/token")]
    [InlineData("https://attacker@groupsplit.sussman.win/join/token")]
    [InlineData("http://groupsplit.sussman.win/join/token")]
    [InlineData("https://groupsplit.sussman.win:8443/join/token")]
    [InlineData("https://groupsplit.sussman.win/groups/123")]
    [InlineData("https://groupsplit.sussman.win/join/token/extra")]
    [InlineData("https://groupsplit.sussman.win/join/token?returnUrl=https://attacker.example")]
    [InlineData("https://groupsplit.sussman.win/join/token#extra")]
    [InlineData("https://groupsplit.sussman.win/claim/a%2Fb")]
    [InlineData("https://groupsplit.sussman.win/join/")]
    [InlineData("groupsplit://auth/callback?code=private")]
    [InlineData("groupsplit://invite/login")]
    public void Untrusted_links_cannot_navigate_the_app(string link) =>
        Assert.Null(NativeLinkRouter.Route(new Uri(link), Website));

    [Fact]
    public void Cold_launch_link_waits_for_the_webview_and_new_links_notify_the_live_view()
    {
        var options = NativeOptions.Create("https://api.example/", "https://auth.example/", false);
        var links = new NativeLinkRouter(options);
        Assert.True(links.Open(new Uri(Website, "join/first")));
        Assert.False(links.Open(new Uri("https://attacker.example/join/other")));
        Assert.Equal("/join/first", links.Take());
        Assert.Null(links.Take());
        var notifications = 0;
        links.Changed += route => { notifications++; Assert.Equal("/claim/second", route); };
        Assert.True(links.Open(new Uri(Website, "claim/second")));
        Assert.Equal(1, notifications);
        Assert.Equal("/claim/second", links.Take());
    }

    [Fact]
    public void Native_shared_links_use_the_public_website_instead_of_the_webview_origin()
    {
        var links = new AppLinks(Website);
        Assert.Equal("https://groupsplit.sussman.win/claim/private-token", links.PublicUrl("https://0.0.0.1/", "/claim/private-token"));
        Assert.Equal("https://local.example/join/token", new AppLinks().PublicUrl("https://local.example/", "/join/token"));
    }
}

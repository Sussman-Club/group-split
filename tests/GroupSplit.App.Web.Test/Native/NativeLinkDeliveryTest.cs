using GroupSplit.App.Services;

namespace GroupSplit.App.Web.Test.Native;

public sealed class NativeLinkDeliveryTest
{
    [Fact]
    public void An_old_view_cannot_consume_the_destination_before_the_visible_view_receives_it()
    {
        var links = new NativeLinkRouter(NativeOptions.Create("https://api.example/", "https://auth.example/", false));
        string? visibleDestination = null;
        links.Changed += _ => links.Take();
        links.Changed += route => visibleDestination = route;
        links.Open(new Uri("groupsplit://invite/claim/invitation-token"));
        Assert.Equal("/claim/invitation-token", visibleDestination);
    }
}

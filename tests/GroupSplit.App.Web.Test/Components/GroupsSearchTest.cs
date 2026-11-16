using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class GroupsSearchTest : ComponentTest
{
    [Fact]
    public void The_whole_group_card_links_to_the_group_and_shows_each_balance()
    {
        var group = new GroupResponse(Guid.NewGuid(), "Trip", 2);
        var groups = new Mock<IGroupsPageStateService>();
        groups.SetupGet(x => x.IsReadyTask).Returns(Task.CompletedTask);
        groups.SetupGet(x => x.Groups).Returns([group]);
        groups.SetupGet(x => x.Position).Returns(new UserPositionResponse(0, 0, 0,
        [
            new GroupPosition(group.Id, group.Name, 10, false) { Currency = "USD" },
            new GroupPosition(group.Id, group.Name, -5, false) { Currency = "EUR" }
        ]));
        Services.AddSingleton(groups.Object);
        var card = Render<Groups>().Find(".gs-group-card");
        Assert.Equal(2, card.QuerySelectorAll(".foot").Length);
        Assert.Equal("A", card.TagName);
        Assert.Equal($"/groups/{group.Id}", card.GetAttribute("href"));
        Assert.Empty(card.QuerySelectorAll("a"));
        Assert.DoesNotContain("Settle", card.TextContent);
        Assert.Contains("$10.00", card.TextContent);
        Assert.Contains("€5.00", card.TextContent);
    }

    [Fact]
    public async Task First_shared_group_guidance_appears_only_when_not_searching()
    {
        var groups = new Mock<IGroupsPageStateService>();
        groups.SetupGet(x => x.IsReadyTask).Returns(Task.CompletedTask);
        groups.SetupGet(x => x.Groups).Returns([
            new GroupResponse(Guid.NewGuid(), "Personal", 1) { IsPersonal = true }]);
        groups.SetupGet(x => x.Position).Returns(new UserPositionResponse(0, 0, 0, []));
        Services.AddSingleton(groups.Object);

        var page = Render<Groups>();
        Assert.Contains("A home, with the bills made clear.", page.Markup);
        Assert.Contains("Create a group for your home", page.Markup);

        await page.Find("input").InputAsync(new()
        {
            Value = "missing"
        });
        page.WaitForAssertion(() => Assert.DoesNotContain("A home, with the bills made clear.", page.Markup));
    }

    [Fact]
    public async Task Search_reveals_archived_matches_and_keeps_its_query_in_the_url()
    {
        var groups = new Mock<IGroupsPageStateService>();
        groups.SetupGet(x => x.IsReadyTask).Returns(Task.CompletedTask);
        groups.SetupGet(x => x.Groups).Returns([
            new GroupResponse(Guid.NewGuid(), "Home", 3),
            new GroupResponse(Guid.NewGuid(), "Málaga trip", 2, true)]);
        groups.SetupGet(x => x.Position).Returns(new UserPositionResponse(0, 0, 0, []));
        Services.AddSingleton(groups.Object);
        var page = Render<Groups>();
        Assert.False(page.Find("details").HasAttribute("open"));
        await page.Find("input").InputAsync(new()
        {
            Value = "malaga"
        });
        page.WaitForAssertion(() =>
        {
            Assert.True(page.Find("details").HasAttribute("open"));
            Assert.Single(page.FindAll(".gs-group-card"));
            Assert.Contains("Málaga trip", page.Find(".gs-group-card").TextContent);
            Assert.Contains("q=malaga", Services.GetRequiredService<NavigationManager>().Uri);
        });
    }
}

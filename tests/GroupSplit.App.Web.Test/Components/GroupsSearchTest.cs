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
        await page.Find("input").InputAsync(new() { Value = "malaga" });
        page.WaitForAssertion(() =>
        {
            Assert.True(page.Find("details").HasAttribute("open"));
            Assert.Single(page.FindAll(".gs-group-card"));
            Assert.Contains("Málaga trip", page.Find(".gs-group-card").TextContent);
            Assert.Contains("q=malaga", Services.GetRequiredService<NavigationManager>().Uri);
        });
    }
}

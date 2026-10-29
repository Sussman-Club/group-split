using Bunit;
using Bunit.TestDoubles;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Pages;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.App.Web.Test.Components;

public class HomeStartTest : ComponentTest
{
    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<IUsersClient> _users = new();
    private readonly UserInfo _user = new(Guid.NewGuid(), "Ana", null, null);

    public HomeStartTest()
    {
        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_users.Object);
        ComponentFactories.AddStub<Dashboard>();
    }

    [Fact]
    public void New_account_starts_at_groups()
    {
        Groups([new GroupResponse(_user.Id, "Personal", 1) { IsPersonal = true }]);
        Activity(0);
        Render<Home>(p => p.AddCascadingValue(_user));
        Assert.EndsWith("/groups", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Existing_group_or_activity_stays_on_home(bool hasSharedGroup, bool hasActivity)
    {
        var groups = new List<GroupResponse> { new(_user.Id, "Personal", 1) { IsPersonal = true } };
        if (hasSharedGroup) groups.Add(new GroupResponse(Guid.NewGuid(), "Trip", 2, IsArchive: true));
        Groups(groups);
        Activity(hasActivity ? 1 : 0);
        var page = Render<Home>(p => p.AddCascadingValue(_user));
        Assert.Single(page.FindComponents<Stub<Dashboard>>());
        Assert.EndsWith("/", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Failed_read_does_not_redirect()
    {
        _groups.Setup(x => x.GetGroupsAsync(1, 2, null, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var page = Render<Home>(p => p.AddCascadingValue(_user));
        Assert.Single(page.FindComponents<Stub<Dashboard>>());
        Assert.EndsWith("/", Services.GetRequiredService<NavigationManager>().Uri);
    }

    private void Groups(ICollection<GroupResponse> groups) => _groups
        .Setup(x => x.GetGroupsAsync(1, 2, null, It.IsAny<CancellationToken>())).ReturnsAsync(groups);
    private void Activity(int count) => _users
        .Setup(x => x.GetCurrentUserActivityAsync(1, 1, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new PagedResponseOfUserActivityResponse([], 1, 1, count));
}


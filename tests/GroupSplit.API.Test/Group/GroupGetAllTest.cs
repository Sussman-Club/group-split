using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for GET /groups endpoint via GroupService.GetAllGroups
/// </summary>
public class GroupGetAllTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// A new account is in no groups at all. It used to be in one -- a hidden "Personal"
    /// group provisioned with it, so that an expense of one's own had somewhere to go --
    /// and that group was a chip in the switcher, a card on the home page, and one extra on
    /// every count of somebody's groups.
    /// </summary>
    [Fact]
    public async Task GetAllGroups_WhenTheAccountIsNew_ReturnsNothing()
    {
        var groupService = GetService<IGroupService>();

        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        Assert.Empty(groups);
    }

    [Fact]
    public async Task GetAllGroups_ReturnsExactlyTheGroupsThatWereMade()
    {
        var groupService = GetService<IGroupService>();

        await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group 1" },
            TestContext.Current.CancellationToken);
        await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group 2" },
            TestContext.Current.CancellationToken);

        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Name == "Test Group 1");
        Assert.Contains(groups, g => g.Name == "Test Group 2");
    }

    [Fact]
    public async Task GetAllGroups_OnlyReturnsGroupsForCurrentUser()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        await groupService.CreateGroup(new CreateGroupRequest { Name = "My Group" },
            TestContext.Current.CancellationToken);

        var otherScope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(otherScope.ServiceProvider);
        await otherScope.ServiceProvider.GetRequiredService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Their Group" },
                TestContext.Current.CancellationToken);

        // Act
        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        // Assert
        Assert.Single(groups);
        Assert.Equal("My Group", groups[0].Name);

        otherScope.Dispose();
    }
}

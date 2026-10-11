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
    /// A new account is in one group: its personal one, provisioned with it so that an
    /// expense of one's own has somewhere to go, and keyed on the account's own id.
    /// </summary>
    [Fact]
    public async Task GetAllGroups_WhenTheAccountIsNew_ReturnsOnlyThePersonalGroup()
    {
        var groupService = GetService<IGroupService>();
        var me = GetService<ICurrentUser>().User.Id;

        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        var personal = Assert.Single(groups);
        Assert.Equal(me, personal.Id);
        Assert.Equal("Personal", personal.Name);
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

        // The two that were made, and the personal group every account has.
        Assert.Equal(3, groups.Count);
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

        // Assert: theirs is not here, and neither is their personal group.
        Assert.Equal(["My Group", "Personal"], groups.Select(g => g.Name).Order());

        otherScope.Dispose();
    }
}

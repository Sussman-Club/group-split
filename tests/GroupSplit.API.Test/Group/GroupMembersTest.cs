using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for GET /groups/members via GroupService.GetGroupMembers
/// </summary>
public class GroupMembersTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// The person who made the group is in it, and is the only one until somebody accepts
    /// an invitation.
    /// </summary>
    [Fact]
    public async Task GetGroupMembers_ANewGroupHoldsItsCreatorAndNobodyElse()
    {
        var groupService = GetService<IGroupService>();
        var user = GetService<ICurrentUser>().User;

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "Just made" },
            TestContext.Current.CancellationToken);

        var membersQuery = await groupService.GetGroupMembers(group.Id, TestContext.Current.CancellationToken);
        var members = await membersQuery.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(members);
        Assert.Equal(user.Id, members[0].Id);
    }

    [Fact]
    public async Task GetGroupMembers_ReturnsMembersOfGroup()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();
        // Create an additional group for the current user
        var createdGroup = await groupService.CreateGroup(new CreateGroupRequest { Name = "My Extra Group" },
            TestContext.Current.CancellationToken);
        // Act
        var membersQuery = await groupService.GetGroupMembers(createdGroup.Id, TestContext.Current.CancellationToken);
        var members = await membersQuery.ToListAsync(TestContext.Current.CancellationToken);
        // Assert
        Assert.Single(members);
        var user = userService.User;
        Assert.Equal(user.Id, members[0].Id);
    }
}

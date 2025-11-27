using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for GET /groups/{id} via GroupService.GetGroupById
/// </summary>
public class GroupMembersTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task GetGroupMembers_MembersOfPersonalGroup()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();
        // Ensure user exists (creates personal group implicitly)
        var user = await userService.GetCurrentUser();
        var personalGroupId = user.PersonalGroup.Id;
        // Act
        var membersQuery = await groupService.GetGroupMembers(personalGroupId, TestContext.Current.CancellationToken);
        var members = await membersQuery.ToListAsync(TestContext.Current.CancellationToken);
        // Assert
        Assert.Single(members);
        Assert.Equal(user.Id, members[0].Id);
    }

    [Fact]
    public async Task GetGroupMembers_ReturnsMembersOfGroup()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();
        // Create an additional group for the current user
        var createdGroup = await groupService.CreateGroup(new CreateGroupRequest { Name = "My Extra Group" },
            TestContext.Current.CancellationToken);
        // Act
        var membersQuery = await groupService.GetGroupMembers(createdGroup.Id, TestContext.Current.CancellationToken);
        var members = await membersQuery.ToListAsync(TestContext.Current.CancellationToken);
        // Assert
        Assert.Single(members);
        var user = await userService.GetCurrentUser();
        Assert.Equal(user.Id, members[0].Id);
    }
}

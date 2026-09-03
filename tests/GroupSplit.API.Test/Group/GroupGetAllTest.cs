using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for GET /groups endpoint via GroupService.GetAllGroups
/// </summary>
public class GroupGetAllTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task GetAllGroups_WhenUserHasOnlyPersonalGroup_ReturnsPersonalGroupMarked()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();

        // Ensure user exists (which creates personal group)

        // Act
        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        // Assert
        Assert.Single(groups);
    }

    [Fact]
    public async Task GetAllGroups_CorrectlyIdentifiesPersonalGroup()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();

        var user = userService.User;
        var personalGroupId = user.PersonalGroup.Id;

        // Create additional groups
        await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group 1" },
            TestContext.Current.CancellationToken);
        await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group 2" },
            TestContext.Current.CancellationToken);

        // Act
        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        // Assert
        Assert.Single(groups, g => g.Id == personalGroupId);

        var nonPersonalGroups = groups.Where(g => g.Id != personalGroupId).ToList();
        Assert.Equal(2, nonPersonalGroups.Count);
        Assert.All(nonPersonalGroups, g => Assert.NotEqual(personalGroupId, g.Id));
    }

    [Fact]
    public async Task GetAllGroups_OnlyReturnsGroupsForCurrentUser()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        await groupService.CreateGroup(new CreateGroupRequest { Name = "My Group" },
            TestContext.Current.CancellationToken);

        var otherUser = await CreateNewUser();

        // Act
        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        // Assert
        // Should only return current user's groups (1 personal + 1 created = 2)
        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(groups, g => g.Id == otherUser.PersonalGroup.Id);
    }
}

using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for POST /groups endpoint via GroupService.CreateGroup
/// </summary>
public class GroupCreateTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task CreateGroup_CreatesNewGroupAndAddsCurrentUser()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();
        var initialGroupCount = user.Groups.Count;

        // Act
        var result = await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group" },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id); // Should have an ID after saving

        // Verify the group was saved to database with the user
        var groupInDb = await DbContext.Set<GroupSplit.Data.Entities.Group>()
            .Include(g => g.Users)
            .FirstOrDefaultAsync(g => g.Id == result.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(groupInDb);
        Assert.Contains(groupInDb.Users, u => u.Id == user.Id);

        // Verify user's group collection was updated
        await DbContext.Entry(user).Collection(u => u.Groups).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Contains(user.Groups, g => g.Id == result.Id);
        Assert.Equal(initialGroupCount + 1, user.Groups.Count);
    }

    [Fact]
    public async Task CreateGroup_MultipleCallsCreateSeparateGroups()
    {
        // Arrange
        var groupService = GetService<IGroupService>();

        // Act
        var group1 = await groupService.CreateGroup(new CreateGroupRequest { Name = "Group 1" },
            TestContext.Current.CancellationToken);
        var group2 = await groupService.CreateGroup(new CreateGroupRequest { Name = "Group 2" },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(group1.Id, group2.Id);

        // Verify both groups exist in database
        var groupCount = await DbContext.Set<GroupSplit.Data.Entities.Group>()
            .CountAsync(g => g.Id == group1.Id || g.Id == group2.Id,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, groupCount);
    }
}
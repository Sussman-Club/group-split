using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for GET /groups/{id} via GroupService.GetGroupById
/// </summary>
public class GroupGetByIdTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task GetGroupById_ReturnsGroupForCurrentUser()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();

        // Ensure user exists (creates personal group implicitly)

        // Create an additional group for the current user
        var created = await groupService.CreateGroup(new CreateGroupRequest { Name = "My Extra Group" },
            TestContext.Current.CancellationToken);

        // Act
        var result = await groupService.GetGroupById(created.Id, TestContext.Current.CancellationToken);
        var group = await result.FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(group);
        Assert.Equal(created.Id, group.Id);
    }

    [Fact]
    public async Task GetGroupById_NonExistentId_ReturnsNull()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();

        // Ensure user exists

        // Act
        var result = await groupService.GetGroupById(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetGroupById_GroupOfAnotherUser_ReturnsNull()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<ICurrentUser>();

        // Ensure current user exists and has at least personal group

        // Create a new user with its own personal group
        var otherUser = await CreateNewUser();

        // Act
        var result = await groupService.GetGroupById(otherUser.PersonalGroup.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }
}

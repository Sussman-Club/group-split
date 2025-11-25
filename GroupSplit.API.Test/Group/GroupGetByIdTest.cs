using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using UserEntity = GroupSplit.Data.Entities.User;

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
        var userService = GetService<IUserService>();

        // Ensure user exists (creates personal group implicitly)
        await userService.GetCurrentUser();

        // Create an additional group for the current user
        var created = await groupService.CreateGroup(new CreateGroupRequest { Name = "My Extra Group" },
            TestContext.Current.CancellationToken);

        // Act
        var result = await groupService.GetGroupById(created.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
    }

    [Fact]
    public async Task GetGroupById_NonExistentId_ReturnsNull()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();

        // Ensure user exists
        await userService.GetCurrentUser();

        // Act
        var result = await groupService.GetGroupById(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetGroupById_GroupOfAnotherUser_ReturnsNull()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();

        // Ensure current user exists and has at least personal group
        await userService.GetCurrentUser();

        // Create a different user and a group that only that user belongs to
        var otherUser = new UserEntity
        {
            Identity = new UserIdentity
            {
                IdentityId = Guid.NewGuid()
                    .ToString()
            },
            PersonalGroup = new GroupSplit.Data.Entities.Group()
            {
                Name = "Personal"
            },
            FirstName = "Random"
        };

        var otherUserGroup = new GroupSplit.Data.Entities.Group
        {
            Name = "Other Group",
            Users = { otherUser }
        };

        DbContext.Set<UserEntity>().Add(otherUser);
        DbContext.Set<GroupSplit.Data.Entities.Group>().Add(otherUserGroup);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await groupService.GetGroupById(otherUserGroup.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }
}
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
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

        // A group made by somebody else, which the caller is not in.
        using var otherScope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(otherScope.ServiceProvider);

        var theirs = await otherScope.ServiceProvider.GetRequiredService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Not yours" },
                TestContext.Current.CancellationToken);

        // Act
        var result = await groupService.GetGroupById(theirs.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }
}

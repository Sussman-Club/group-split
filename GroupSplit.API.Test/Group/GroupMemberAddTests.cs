using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for POST /groups/members via GroupService.AddGroupMembers
/// </summary>
public class GroupMemberAddTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task AddGroupMembers_AddsMembersToGroup()
    {
        // Arrange
        var groupService = GetService<IGroupService>();

        var group = await groupService.CreateGroup(new CreateGroupRequest
        {
            Name = "Test Group",
        }, TestContext.Current.CancellationToken);

        var newUser = await CreateNewUser();

        var exception = await Record.ExceptionAsync(async () =>
        {
            // Act
            await groupService.AddGroupMembers(group.Id, new AddMemberRequest(
                [new UserIdentifier { Email = newUser.Email! }]
            ), TestContext.Current.CancellationToken);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task AddGroupMembers_NonExistentGroup_ThrowsArgumentException()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var newUser = await CreateNewUser();
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await groupService.AddGroupMembers(Guid.NewGuid(), new AddMemberRequest(
                [new UserIdentifier { Email = newUser.Email! }]
            ), TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task AddGroupMembers_EmailsNotFound_NoUsersAdded()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new CreateGroupRequest
        {
            Name = "Test Group",
        }, TestContext.Current.CancellationToken);

        // Act
        await groupService.AddGroupMembers(group.Id, new AddMemberRequest(
            [new UserIdentifier { Email = "wrong@test.com" }]), TestContext.Current.CancellationToken);

        var members = await groupService.GetGroupMembers(group.Id, TestContext.Current.CancellationToken);
        // Assert
        Assert.Single(members);
    }
}
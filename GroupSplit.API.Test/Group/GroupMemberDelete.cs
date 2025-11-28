using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.ApplicationModel.Communication;
using System;
using System.Collections.Generic;
using System.Text;

namespace GroupSplit.API.Test.Group;

public class GroupMemberDelete(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task DeleteGroupMember_DeleteValidMember()
    {
        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new ()
        {
            Name = "Test Group",
        }, TestContext.Current.CancellationToken);

        var newMember = await CreateNewUser();

        await groupService.AddGroupMembers(group.Id, new AddMemberRequest
        (
            [ new UserIdentifier() { Email = newMember.Email! } ]
        ), TestContext.Current.CancellationToken);

        var membersBeforeDelete = await (await groupService.GetGroupMembers(group.Id, TestContext.Current.CancellationToken))
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(membersBeforeDelete, m => m.Id == newMember.Id);

        await groupService.RemoveGroupMember(group.Id, newMember.Id, TestContext.Current.CancellationToken);

        var membersAfterDelete = await (await groupService.GetGroupMembers(group.Id, TestContext.Current.CancellationToken))
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(membersAfterDelete, m => m.Id == newMember.Id);
        Assert.Superset(membersAfterDelete.Select(m => m.Id).ToHashSet(), membersBeforeDelete.Select(m => m.Id).ToHashSet());
    }

    [Fact]
    public async Task DeleteGroupMember_CannotRemoveCurrentUser()
    {
        // Arrange
        var userService = GetService<IUserService>();
        var groupService = GetService<IGroupService>();

        var currentUser = await userService.GetCurrentUser();
        var group = await groupService.CreateGroup(new()
        {
            Name = "Test Group",
        }, TestContext.Current.CancellationToken);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await groupService.RemoveGroupMember(
                group.Id,
                currentUser.Id,
                TestContext.Current.CancellationToken);
        });

        Assert.Equal("Cannot remove current user from group", exception.Message);
    }
}

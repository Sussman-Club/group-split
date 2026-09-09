using GroupSplit.API.Services;
using GroupSplit.Shared.Errors;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

public class GroupMemberDelete(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task DeleteGroupMember_DeleteValidMember()
    {
        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new CreateGroupRequest
        {
            Name = "Test Group",
        }, TestContext.Current.CancellationToken);

        var newMember = await CreateNewUser();

        await JoinGroup(group.Id, newMember);

        var membersBeforeDelete =
            await (await groupService.GetGroupMembers(group.Id, TestContext.Current.CancellationToken))
                .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(membersBeforeDelete, m => m.Id == newMember.Id);

        await groupService.RemoveGroupMember(group.Id, newMember.Id, TestContext.Current.CancellationToken);

        var membersAfterDelete =
            await (await groupService.GetGroupMembers(group.Id, TestContext.Current.CancellationToken))
                .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(membersAfterDelete, m => m.Id == newMember.Id);
        Assert.Superset(membersAfterDelete.Select(m => m.Id).ToHashSet(),
            membersBeforeDelete.Select(m => m.Id).ToHashSet());
    }

    /// <summary>
    /// A rule that still named a departed member would keep giving them a share of every
    /// later expense, so the participant row goes with them -- and the shares of whoever is
    /// left are untouched, because the division normalises by the total it is given.
    /// </summary>
    /// <remarks>
    /// Pinned because it is what <c>docs/split-rules-and-membership.md</c> says is true, and
    /// what the rule editor relies on to only have a stale membership to guard against
    /// rather than a stored rule that names strangers by design. See issue #184.
    /// </remarks>
    [Fact]
    public async Task DeleteGroupMember_TakesThemOutOfTheRulesThatNameThem()
    {
        var ct = TestContext.Current.CancellationToken;

        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group" }, ct);

        var staying = GetService<ICurrentUser>().User;
        var leaving = await CreateNewUser();

        await JoinGroup(group.Id, leaving);

        var rule = await GetService<ISplitRuleService>().Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Groceries",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [staying.Id] = 1, [leaving.Id] = 1 }
            }
        }, ct);

        await groupService.RemoveGroupMember(group.Id, leaving.Id, ct);

        var participants = await DbContext.Set<Data.Entities.SplitRuleParticipant>()
            .Where(participant => participant.SplitRuleId == rule.Id)
            .ToListAsync(ct);

        Assert.Equal([staying.Id], participants.Select(participant => participant.UserId));
        Assert.Equal(1, participants.Single().Weight);
    }

    [Fact]
    public async Task DeleteGroupMember_CannotRemoveCurrentUser()
    {
        // Arrange
        var userService = GetService<ICurrentUser>();
        var groupService = GetService<IGroupService>();

        var currentUser = userService.User;
        var group = await groupService.CreateGroup(new CreateGroupRequest
        {
            Name = "Test Group",
        }, TestContext.Current.CancellationToken);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ForbiddenException>(async () =>
        {
            await groupService.RemoveGroupMember(
                group.Id,
                currentUser.Id,
                TestContext.Current.CancellationToken);
        });

        Assert.Equal(ErrorCodes.GroupCannotRemoveSelf, exception.Code);
    }
}
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

        // What the rule says now. A departure opens a new version rather than editing the
        // one the rule was on, so the superseded version still names them -- which is the
        // point, and is asserted below.
        var participants = await DbContext.Set<Data.Entities.SplitRuleParticipant>()
            .Where(participant => participant.SplitRuleVersion.SplitRuleId == rule.Id &&
                                  participant.SplitRuleVersion.SupersededAt == null)
            .ToListAsync(ct);

        Assert.Equal([staying.Id], participants.Select(participant => participant.UserId));
        Assert.Equal(1, participants.Single().Weight);

        // And the version every expense recorded before they left points at goes on naming
        // them, so those expenses can still be divided again by the rule they had.
        var before = await DbContext.Set<Data.Entities.SplitRuleParticipant>()
            .Where(participant => participant.SplitRuleVersion.SplitRuleId == rule.Id &&
                                  participant.SplitRuleVersion.SupersededAt != null)
            .Select(participant => participant.UserId)
            .ToListAsync(ct);

        Assert.Equal(2, before.Count);
        Assert.Contains(staying.Id, before);
        Assert.Contains(leaving.Id, before);
    }

    /// <summary>
    /// A member who still owes the group, or is still owed by it, cannot be removed -- and
    /// the refusal carries the figure.
    /// </summary>
    /// <remarks>
    /// Issue #246. The same rule <c>Leave</c> applies to somebody walking out applies to
    /// somebody being shown the door, and it was only covered on the walking-out side.
    /// Removal is the half that can be done <em>to</em> a person, which makes it the half
    /// where a silent write would be worst: the balances of a group sum to zero, so
    /// dropping a participant who is not square either leaves a column that no longer adds
    /// up or moves their debt onto nobody.
    /// <para>
    /// The balance rides on the problem rather than only in the sentence, because the app
    /// puts it in front of somebody as an amount to settle and the CLI prints it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Removing_a_member_who_has_not_settled_up_is_refused_and_names_the_balance()
    {
        var ct = TestContext.Current.CancellationToken;

        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "The flat" }, ct);

        var me = GetService<ICurrentUser>().User;
        var leaving = await CreateNewUser();

        await JoinGroup(group.Id, leaving);

        // They paid for it, so the group owes them half: a balance of +50.
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            PaidByUserId = leaving.Id
        }, ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            groupService.RemoveGroupMember(group.Id, leaving.Id, ct));

        Assert.Equal(ErrorCodes.GroupMemberNotSettled, refused.Code);
        Assert.Equal(50m, Assert.IsType<decimal>(refused.Extensions["balance"]));

        // And nothing happened: they are still a member, which is what makes settling up
        // and trying again possible.
        var members = await (await groupService.GetGroupMembers(group.Id, ct)).ToListAsync(ct);

        Assert.Contains(members, member => member.Id == leaving.Id);
        Assert.Contains(members, member => member.Id == me.Id);
    }

    /// <summary>
    /// What a departed member did is still there afterwards, under their own name, and the
    /// group's balances still add up.
    /// </summary>
    /// <remarks>
    /// Issue #246, the data-integrity half. A departure changes who may be given a share
    /// from now on; it may not restate what anybody owed, because every balance in the
    /// group is a sum over those rows and the history is what people settle up against.
    /// <para>
    /// So the expense keeps its amount, its payer and its shares -- the departed member's
    /// share included -- and the group's remaining balances go on summing to zero, which is
    /// the invariant that says nothing was quietly dropped or moved.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task What_a_departed_member_paid_and_owed_survives_their_removal()
    {
        var ct = TestContext.Current.CancellationToken;

        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "The flat" }, ct);

        var me = GetService<ICurrentUser>().User;
        var leaving = await CreateNewUser();

        await JoinGroup(group.Id, leaving);

        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            PaidByUserId = me.Id
        }, ct);

        // Square: they pay back the half they owe, which is the state removal requires.
        await groupService.Settle(group.Id, new SettleRequest
        {
            UserId = leaving.Id,
            Amount = 50m,
            Direction = SettlementDirection.TheyPaidYou,
            Date = DateTimeOffset.UtcNow
        }, ct);

        await groupService.RemoveGroupMember(group.Id, leaving.Id, ct);

        // The expense is untouched, and still says who owed what -- their row included.
        var splits = await DbContext.Set<Data.Entities.TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(ct);

        Assert.Equal(100m, splits.Sum(split => split.Amount));
        Assert.Equal(50m, splits.Single(split => split.UserId == leaving.Id).Amount);

        // And the group's own ledger still carries it, with the payer still named.
        var activity = await (await groupService.GetGroupActivity(group.Id, ct)).ToListAsync(ct);

        Assert.Contains(activity, transaction => transaction.Id == expense.Id);

        // The repayment too: a settlement is how the departure became possible, and a
        // balance that moved with nothing in the feed to explain it is its own kind of wrong.
        Assert.Contains(activity, transaction => transaction is Data.Entities.Transfer);

        // The column still adds up. A departed member is no longer a participant, so they
        // are out of the listing -- and because they left square, what is left still sums
        // to zero rather than to whatever they were carrying.
        var balances = await (await groupService.GetGroupNetBalance(group.Id, ct)).ToListAsync(ct);

        Assert.DoesNotContain(balances, balance => balance.UserId == leaving.Id);
        Assert.Equal(0m, balances.Sum(balance => balance.Balance));
    }

    /// <summary>
    /// A departed member is not choosable for a new expense: neither in the roster the
    /// pickers are built from, nor by an id sent straight to the API.
    /// </summary>
    /// <remarks>
    /// Issue #246 asks for the first and gets the second for free, and the second is the
    /// one worth pinning. A listing that stops offering somebody is a convenience; a share
    /// naming them is what would actually go on giving a departed member a position in a
    /// group's balances, and a client with a stale roster -- a dialog left open across the
    /// removal -- is exactly how that arrives.
    /// </remarks>
    [Fact]
    public async Task A_departed_member_cannot_be_given_a_share_of_a_new_expense()
    {
        var ct = TestContext.Current.CancellationToken;

        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "The flat" }, ct);

        var me = GetService<ICurrentUser>().User;
        var leaving = await CreateNewUser();

        await JoinGroup(group.Id, leaving);
        await groupService.RemoveGroupMember(group.Id, leaving.Id, ct);

        // Out of the roster every picker in the app is built from.
        var members = await (await groupService.GetGroupMembers(group.Id, ct)).ToListAsync(ct);

        Assert.DoesNotContain(members, member => member.Id == leaving.Id);

        // And out of reach of a client that kept the old one.
        var refused = await Assert.ThrowsAsync<ConflictException>(async () =>
            await GetService<ITransactionService>().Create(new CreateTransactionRequest
            {
                Name = "Dinner",
                Amount = 40m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = group.Id,
                PaidByUserId = me.Id,
                Splits =
                [
                    new SplitInput { UserId = me.Id, Amount = 20m },
                    new SplitInput { UserId = leaving.Id, Amount = 20m }
                ]
            }, ct));

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, refused.Code);
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
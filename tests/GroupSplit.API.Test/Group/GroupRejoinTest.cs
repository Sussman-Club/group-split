using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Leaving a group, being removed from one, and what it takes to come back.
/// </summary>
/// <remarks>
/// A departure used to delete the membership row, so the group forgot the person had ever
/// been in it. Anything they had seen while they were a member -- the join link, every
/// pending invitation -- still worked for them afterwards, and an invitation also handed
/// them somebody else's place in the ledger. Now the row is closed rather than deleted, and a
/// way back in is only as good as its date: one issued before they went does not bring them
/// back.
/// </remarks>
public class GroupRejoinTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IJoinLinkService Links => GetService<IJoinLinkService>();
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "Lisbon") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    /// <summary>Somebody else, signed in within a scope of their own.</summary>
    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    private static T As<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>The group's membership rows for one person, closed ones included.</summary>
    private Task<List<GroupMembership>> RowsFor(Guid groupId, Guid userId) =>
        DbContext.Set<GroupMembership>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.GroupId == groupId && row.UserId == userId)
            .ToListAsync(Ct);

    private async Task<bool> IsMember(Guid groupId, Guid userId)
    {
        // A fresh scope, so nothing this test's own context is tracking can answer for the
        // database.
        using var scope = GetService<IServiceScopeFactory>().CreateScope();

        return await As<Data.AppDbContext>(scope).Set<Data.Entities.Group>()
            .Where(candidate => candidate.Id == groupId)
            .SelectMany(candidate => candidate.Users)
            .AnyAsync(user => user.Id == userId, Ct);
    }

    [Fact]
    public async Task A_member_who_left_cannot_rejoin_through_a_link_made_before_they_left()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        var (scope, leaver) = await AnotherPerson();

        using (scope)
        {
            await As<IJoinLinkService>(scope).Accept(link.Token, Ct);
            await As<IGroupService>(scope).Leave(group.Id, Ct);

            var described = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IJoinLinkService>(scope).Describe(link.Token, Ct));

            Assert.Equal(ErrorCodes.GroupRejoinNeedsNewInvitation, described.Code);
            Assert.Equal("Left", described.Extensions["leftReason"]);
            Assert.NotNull(described.Extensions["leftAt"]);
            Assert.Equal(link.CreatedAt, described.Extensions["issuedAt"]);

            var accepted = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IJoinLinkService>(scope).Accept(link.Token, Ct));

            Assert.Equal(ErrorCodes.GroupRejoinNeedsNewInvitation, accepted.Code);
        }

        Assert.False(await IsMember(group.Id, leaver.Id));
    }

    /// <summary>
    /// The link is the group's, and it is sitting in the group's thread for other people.
    /// Somebody leaving says nothing about them.
    /// </summary>
    [Fact]
    public async Task Leaving_does_not_put_out_the_groups_join_link()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        var (leaving, _) = await AnotherPerson();

        using (leaving)
        {
            await As<IJoinLinkService>(leaving).Accept(link.Token, Ct);
            await As<IGroupService>(leaving).Leave(group.Id, Ct);
        }

        Assert.Equal(link.Token, Assert.Single(await Links.ForGroup(group.Id, Ct)).Token);

        var (arriving, newcomer) = await AnotherPerson();

        using (arriving)
            await As<IJoinLinkService>(arriving).Accept(link.Token, Ct);

        Assert.True(await IsMember(group.Id, newcomer.Id));
    }

    /// <summary>
    /// They could read the link while they were a member and may have passed it on. The
    /// rejoin rule stops them using it; putting it out is what stops the people they gave it
    /// to.
    /// </summary>
    [Fact]
    public async Task Removing_a_member_puts_out_the_groups_live_join_links()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        var (scope, removed) = await AnotherPerson();

        using (scope)
        {
            await As<IJoinLinkService>(scope).Accept(link.Token, Ct);

            await Groups.RemoveGroupMember(group.Id, removed.Id, Ct);

            Assert.Empty(await Links.ForGroup(group.Id, Ct));

            // Their next request, which starts from the database rather than from the link as
            // their last one read it.
            As<Data.AppDbContext>(scope).ChangeTracker.Clear();

            var accepted = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IJoinLinkService>(scope).Accept(link.Token, Ct));

            Assert.Equal(ErrorCodes.GroupJoinLinkRevoked, accepted.Code);
        }

        Assert.False(await IsMember(group.Id, removed.Id));
    }

    /// <summary>
    /// The door this used to leave open widest: claiming an invitation they had seen while
    /// they were a member rejoined them and handed them the stand-in's place in the ledger.
    /// </summary>
    [Fact]
    public async Task A_removed_member_cannot_claim_an_invitation_issued_before_they_were_removed()
    {
        var group = await AGroup();
        var (scope, removed) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, removed);

            var invitation = Assert.Single(await Invitations.Invite(group.Id,
                new InviteToGroupRequest { Names = ["Dani"] }, Ct));

            await Groups.RemoveGroupMember(group.Id, removed.Id, Ct);

            var described = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IInvitationService>(scope).Describe(invitation.Token, Ct));

            Assert.Equal(ErrorCodes.GroupRejoinNeedsNewInvitation, described.Code);
            Assert.Equal("Removed", described.Extensions["leftReason"]);

            var claimed = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IInvitationService>(scope).Claim(invitation.Token, Ct));

            Assert.Equal(ErrorCodes.GroupRejoinNeedsNewInvitation, claimed.Code);

            // Refused before it was remembered: it is not something they are waiting on.
            Assert.Empty(await As<IInvitationService>(scope).Mine(Ct));
        }

        // Still standing for the person it was meant for, with the stand-in behind it.
        var standing = Assert.Single(await Invitations.ForGroup(group.Id, Ct));
        Assert.Equal("Dani", standing.Name);
        Assert.False(await IsMember(group.Id, removed.Id));
    }

    [Fact]
    public async Task A_fresh_link_made_after_the_removal_lets_them_back_in_as_an_ordinary_member()
    {
        var group = await AGroup();
        var (scope, returning) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, returning);
            await Groups.RemoveGroupMember(group.Id, returning.Id, Ct);

            var left = Assert.Single(await RowsFor(group.Id, returning.Id));

            var fresh = await Links.Create(group.Id, Ct);

            var described = await As<IJoinLinkService>(scope).Describe(fresh.Token, Ct);
            Assert.False(described.AlreadyAMember);

            var joined = await As<IJoinLinkService>(scope).Accept(fresh.Token, Ct);

            Assert.False(joined.AlreadyAMember);
            Assert.Equal(2, joined.MemberCount);

            // One row, opened again, with nothing of the departure left on it.
            var row = Assert.Single(await RowsFor(group.Id, returning.Id));

            Assert.Null(row.LeftAt);
            Assert.Null(row.LeftReason);
            Assert.Null(row.LeftByUserId);
            Assert.True(row.JoinedAt > left.LeftAt);
        }

        Assert.True(await IsMember(group.Id, returning.Id));

        // Back with the one rule every member has, not a second one.
        var theirs = await DbContext.Set<SplitRule>()
            .Where(rule => rule.BuiltIn && rule.Group.Id == group.Id)
            .Where(rule => rule.Versions.Any(version =>
                version.SupersededAt == null &&
                (version as SoleSplitRuleVersion)!.UserId == returning.Id))
            .CountAsync(Ct);

        Assert.Equal(1, theirs);
    }

    [Fact]
    public async Task A_fresh_invitation_issued_after_they_left_can_be_claimed()
    {
        var group = await AGroup();
        var (scope, returning) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, returning);
            await As<IGroupService>(scope).Leave(group.Id, Ct);

            var invitation = Assert.Single(await Invitations.Invite(group.Id,
                new InviteToGroupRequest { Names = ["Omar"] }, Ct));

            var claimed = await As<IInvitationService>(scope).Claim(invitation.Token, Ct);

            Assert.Equal(group.Id, claimed.GroupId);
        }

        Assert.True(await IsMember(group.Id, returning.Id));
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task A_departure_keeps_the_membership_row_and_says_when_and_why()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;
        var removed = await CreateNewUser();

        await JoinGroup(group.Id, removed);

        var before = DateTimeOffset.UtcNow;
        await Groups.RemoveGroupMember(group.Id, removed.Id, Ct);

        var row = Assert.Single(await RowsFor(group.Id, removed.Id));

        Assert.NotNull(row.LeftAt);
        Assert.True(row.LeftAt >= before);
        Assert.Equal(LeftReason.Removed, row.LeftReason);
        Assert.Equal(me.Id, row.LeftByUserId);

        // And every read that does not ask for history does not see it.
        Assert.False(await DbContext.Set<GroupMembership>()
            .AnyAsync(candidate => candidate.GroupId == group.Id && candidate.UserId == removed.Id, Ct));
    }

    [Fact]
    public async Task Leaving_records_that_they_left_of_their_own_accord()
    {
        var group = await AGroup();
        var (scope, leaver) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, leaver);
            await As<IGroupService>(scope).Leave(group.Id, Ct);
        }

        var row = Assert.Single(await RowsFor(group.Id, leaver.Id));

        Assert.Equal(LeftReason.Left, row.LeftReason);
        Assert.Equal(leaver.Id, row.LeftByUserId);
    }

    [Fact]
    public async Task A_departed_member_is_not_counted_or_listed_anywhere_current_members_are()
    {
        var group = await AGroup();
        var (scope, removed) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, removed);
            await Groups.RemoveGroupMember(group.Id, removed.Id, Ct);

            // Their own view: the group is gone from it, and only their personal one is left.
            Assert.Equal(removed.Id,
                Assert.Single(await (await As<IGroupService>(scope).GetAllGroups(Ct)).ToListAsync(Ct)).Id);
            Assert.Empty(await As<IGroupService>(scope).GetGroupById(group.Id, Ct));
            Assert.DoesNotContain((await As<IGroupService>(scope).GetPosition(Ct)).Groups,
                position => position.GroupId == group.Id);
        }

        // The group's view: they are not in it, nor in its balances.
        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);
        Assert.DoesNotContain(members, member => member.Id == removed.Id);

        var balances = await (await Groups.GetGroupNetBalance(group.Id, Ct)).ToListAsync(Ct);
        Assert.DoesNotContain(balances, balance => balance.UserId == removed.Id);

        using var fresh = GetService<IServiceScopeFactory>().CreateScope();
        var loaded = await As<Data.AppDbContext>(fresh).Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == group.Id, Ct);

        Assert.DoesNotContain(loaded.Users, user => user.Id == removed.Id);
        Assert.Single(loaded.Users);

        Assert.Equal(ParticipantStanding.Departed,
            await GetService<IGroupParticipants>().StandingOf(group.Id, removed.Id, Ct));
    }

    [Fact]
    public async Task Removing_somebody_who_is_not_in_the_group_is_a_404()
    {
        var group = await AGroup();
        var stranger = await CreateNewUser();

        var never = await Assert.ThrowsAsync<NotFoundException>(() =>
            Groups.RemoveGroupMember(group.Id, stranger.Id, Ct));

        Assert.Equal(ErrorCodes.UserNotFound, never.Code);

        // Somebody already gone used to be "removed" again, successfully, with nothing
        // changing. There is no membership to remove.
        var departed = await CreateNewUser();
        await JoinGroup(group.Id, departed);
        await Groups.RemoveGroupMember(group.Id, departed.Id, Ct);

        var again = await Assert.ThrowsAsync<NotFoundException>(() =>
            Groups.RemoveGroupMember(group.Id, departed.Id, Ct));

        Assert.Equal(ErrorCodes.UserNotFound, again.Code);
    }

    /// <summary>
    /// Six services each ask "is the caller in this group" with a check of their own. They
    /// all read Group.Users, and the membership filter is what makes that mean a current
    /// member; this pins them together until they are folded into one.
    /// </summary>
    [Fact]
    public async Task A_departed_member_is_a_stranger_to_every_service()
    {
        var group = await AGroup();
        var (scope, departed) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, departed);
            await Groups.RemoveGroupMember(group.Id, departed.Id, Ct);

            Assert.Empty(await As<IGroupService>(scope).GetGroupById(group.Id, Ct));

            async Task RefusedAsNotFound(Func<Task> act)
            {
                var refused = await Assert.ThrowsAsync<NotFoundException>(act);
                Assert.Equal(ErrorCodes.GroupNotFound, refused.Code);
            }

            await RefusedAsNotFound(() => As<IGroupService>(scope).Leave(group.Id, Ct));

            await RefusedAsNotFound(() => As<ICategoryService>(scope).Create(new CreateCategoryRequest
            {
                GroupId = group.Id,
                Name = "Sneaking back"
            }, Ct));

            await RefusedAsNotFound(() => As<ISplitRuleService>(scope).Create(new CreateSplitRuleRequest
            {
                GroupId = group.Id,
                Name = "Sneaking back",
                Definition = new EvenSplitRuleDto()
            }, Ct));

            await RefusedAsNotFound(() => As<ITransactionService>(scope).Create(new CreateTransactionRequest
            {
                Name = "Sneaking back",
                Amount = 10m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = group.Id
            }, Ct).AsTask());

            await RefusedAsNotFound(() => As<IInvitationService>(scope).Invite(group.Id,
                new InviteToGroupRequest { Names = ["A friend"] }, Ct));

            await RefusedAsNotFound(() => As<IJoinLinkService>(scope).Create(group.Id, Ct));

            Assert.Empty(await As<IInvitationService>(scope).ForGroup(group.Id, Ct));
        }
    }
}

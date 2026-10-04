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
/// What the schema now records and keeps straight about a group's people: the history of
/// each membership, what kind of row a user is, whose "all for one" rule is whose, and the
/// one join link in force.
/// </summary>
/// <remarks>
/// The constraints that back these are Postgres's and are exercised against it in
/// <c>GroupSplit.AppHost.Test</c>. These are the service paths that have to write the new
/// columns correctly in the first place.
/// </remarks>
public class GroupSchemaIntegrityTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IJoinLinkService Links => GetService<IJoinLinkService>();
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private Guid Self => GetService<ICurrentUser>().User.Id;

    private Task<Data.Entities.Group> AGroup(string name = "Lisbon") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    private static T As<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>The group's history, read from a scope of its own so nothing tracked answers.</summary>
    private async Task<List<GroupMembershipEvent>> HistoryOf(Guid groupId)
    {
        using var scope = GetService<IServiceScopeFactory>().CreateScope();

        return await As<Data.AppDbContext>(scope).Set<GroupMembershipEvent>()
            .AsNoTracking()
            .Where(change => change.GroupId == groupId)
            .OrderBy(change => change.At)
            .ToListAsync(Ct);
    }

    private async Task<T> Fresh<T>(Func<Data.AppDbContext, Task<T>> read)
    {
        using var scope = GetService<IServiceScopeFactory>().CreateScope();

        return await read(As<Data.AppDbContext>(scope));
    }

    // ---- Membership history -------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_group_records_its_creator_joining()
    {
        var group = await AGroup();

        var joined = Assert.Single(await HistoryOf(group.Id));

        Assert.Equal(MembershipEventKind.Joined, joined.Kind);
        Assert.Equal(Self, joined.UserId);
        Assert.Equal(Self, joined.ByUserId);
    }

    /// <summary>
    /// The case the single membership row could not keep: removed, then back. The row now
    /// says only that they are a member; the history says how they got here, and who
    /// removed them in between.
    /// </summary>
    [Fact]
    public async Task A_removal_and_a_return_are_both_kept_after_the_row_reopens()
    {
        var group = await AGroup();
        var first = await Links.Create(group.Id, Ct);

        var (scope, guest) = await AnotherPerson();

        using (scope)
            await As<IJoinLinkService>(scope).Accept(first.Token, Ct);

        await Groups.RemoveGroupMember(group.Id, guest.Id, Ct);

        var second = await Links.Create(group.Id, Ct);

        // A request of its own, as the return would be: the first scope still tracks the
        // row as it was before the removal.
        using (var returning = GetService<IServiceScopeFactory>().CreateScope())
        {
            var user = await As<Data.AppDbContext>(returning).Set<Data.Entities.User>()
                .FirstAsync(candidate => candidate.Id == guest.Id, Ct);

            As<ICurrentUserInitializer>(returning).Initialize(user);

            await As<IJoinLinkService>(returning).Accept(second.Token, Ct);
        }

        var theirs = (await HistoryOf(group.Id)).Where(change => change.UserId == guest.Id).ToList();

        Assert.Equal(
            [MembershipEventKind.Joined, MembershipEventKind.Removed, MembershipEventKind.Rejoined],
            theirs.Select(change => change.Kind));

        Assert.Equal(Self, theirs[1].ByUserId);
        Assert.Equal(guest.Id, theirs[2].ByUserId);
    }

    [Fact]
    public async Task Leaving_is_recorded_as_done_by_the_leaver()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        var (scope, leaver) = await AnotherPerson();

        using (scope)
        {
            await As<IJoinLinkService>(scope).Accept(link.Token, Ct);
            await As<IGroupService>(scope).Leave(group.Id, Ct);
        }

        var left = (await HistoryOf(group.Id)).Last();

        Assert.Equal(MembershipEventKind.Left, left.Kind);
        Assert.Equal(leaver.Id, left.UserId);
        Assert.Equal(leaver.Id, left.ByUserId);
    }

    [Fact]
    public async Task Deleting_an_account_records_the_departure_and_marks_the_row_deleted()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        var (scope, gone) = await AnotherPerson();

        using (scope)
        {
            await As<IJoinLinkService>(scope).Accept(link.Token, Ct);
            Assert.Empty(await As<IAccountService>(scope).DeleteAccount(gone.Id, Ct));
        }

        Assert.Equal(MembershipEventKind.AccountDeleted, (await HistoryOf(group.Id)).Last().Kind);

        var row = await Fresh(db => db.Set<Data.Entities.User>().AsNoTracking()
            .FirstAsync(user => user.Id == gone.Id, Ct));

        Assert.Equal(UserKind.Deleted, row.Kind);
    }

    // ---- What a user row is ------------------------------------------------------------------

    [Fact]
    public async Task An_invitees_stand_in_is_marked_as_one()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Luz"] }, Ct));

        var standIn = await Fresh(db => db.Set<Data.Entities.User>().AsNoTracking()
            .FirstAsync(user => user.Id == invitation.ParticipantUserId, Ct));

        Assert.Equal(UserKind.StandIn, standIn.Kind);
        Assert.Equal(UserKind.Account, GetService<ICurrentUser>().User.Kind);
    }

    /// <summary>
    /// Every way into a group goes through the joiner, and none should ever hand it a
    /// stand-in: claiming moves the stand-in's position onto the claimer's account instead.
    /// So this is a guard against a caller bug, and it says so loudly.
    /// </summary>
    [Fact]
    public async Task The_joiner_refuses_to_make_a_stand_in_a_member()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Luz"] }, Ct));

        var standIn = await DbContext.Set<Data.Entities.User>()
            .FirstAsync(user => user.Id == invitation.ParticipantUserId, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GetService<IGroupJoiner>().Join(group, standIn, DateTimeOffset.UtcNow, Ct));
    }

    // ---- Whose "all for one" rule ------------------------------------------------------------

    private Task<List<SplitRule>> ProvisionedIn(Guid groupId) =>
        Fresh(db => db.Set<SplitRule>().AsNoTracking()
            .Where(rule => rule.Group.Id == groupId && rule.ProvisionedForUserId != null)
            .ToListAsync(Ct));

    [Fact]
    public async Task Each_member_and_invitee_has_exactly_one_rule_provisioned_for_them()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Luz"] }, Ct));

        var provisioned = await ProvisionedIn(group.Id);

        Assert.Single(provisioned, rule => rule.ProvisionedForUserId == Self);
        Assert.Single(provisioned, rule => rule.ProvisionedForUserId == invitation.ParticipantUserId);
        Assert.All(provisioned, rule => Assert.True(rule.BuiltIn));
    }

    [Fact]
    public async Task Claiming_moves_the_invitees_rule_to_the_claimer()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Luz"] }, Ct));

        var (scope, luz) = await AnotherPerson();

        using (scope)
            await As<IInvitationService>(scope).Claim(invitation.Token, Ct);

        var provisioned = await ProvisionedIn(group.Id);

        var hers = Assert.Single(provisioned, rule => rule.ProvisionedForUserId == luz.Id);
        Assert.DoesNotContain(provisioned, rule => rule.ProvisionedForUserId == invitation.ParticipantUserId);

        // Named for the account that claimed it, not for the name the group typed.
        var shown = $"{luz.FirstName} {luz.LastName}".Trim() is { Length: > 0 } name ? name : luz.Email;
        // Cut to fit the column where the name is long, which a test address is.
        Assert.StartsWith(hers.Name, $"All for {shown}");
    }

    /// <summary>
    /// Retired rather than handed over, because the absorber already has a rule of their
    /// own -- and a retired rule is nobody's provisioned rule, which is what keeps theirs the
    /// only one.
    /// </summary>
    [Fact]
    public async Task A_declined_invitees_used_rule_is_retired_and_belongs_to_nobody()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Luz"] }, Ct));

        var theirs = Assert.Single(await ProvisionedIn(group.Id),
            rule => rule.ProvisionedForUserId == invitation.ParticipantUserId);

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Her flight",
            Amount = 120m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            SplitRuleId = theirs.Id
        }, Ct);

        await Invitations.Decline(invitation.Token, Ct);

        var kept = await Fresh(db => db.Set<SplitRule>().AsNoTracking()
            .FirstAsync(rule => rule.Id == theirs.Id, Ct));

        Assert.False(kept.BuiltIn);
        Assert.Null(kept.ProvisionedForUserId);

        // No longer "All for Luz": it puts everything on whoever absorbed her, and says so,
        // beside that member's own rule.
        var mine = Assert.Single(await ProvisionedIn(group.Id), rule => rule.ProvisionedForUserId == Self);
        Assert.Equal($"{mine.Name} (2)", kept.Name);
    }

    // ---- One join link in force --------------------------------------------------------------

    /// <summary>
    /// A link that lapsed on its own is closed when the next one is made -- the database
    /// allows one unrevoked link per group -- but at the moment it lapsed, so whoever opens
    /// it is still told it expired rather than that the group withdrew it.
    /// </summary>
    [Fact]
    public async Task Replacing_a_lapsed_link_closes_it_and_it_still_reads_as_expired()
    {
        var group = await AGroup();
        var old = await Links.Create(group.Id, Ct);

        var stored = await DbContext.Set<GroupJoinLink>().FirstAsync(link => link.Token == old.Token, Ct);
        stored.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        await DbContext.SaveChangesAsync(Ct);

        await Links.Create(group.Id, Ct);

        var links = await Fresh(db => db.Set<GroupJoinLink>().AsNoTracking()
            .Where(link => link.GroupId == group.Id)
            .ToListAsync(Ct));

        Assert.Single(links, link => link.RevokedAt is null);
        Assert.Equal(stored.ExpiresAt, links.Single(link => link.Token == old.Token).RevokedAt);

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var refused = await Assert.ThrowsAsync<ConflictException>(() =>
                As<IJoinLinkService>(scope).Describe(old.Token, Ct));

            Assert.Equal(ErrorCodes.GroupJoinLinkExpired, refused.Code);
        }
    }
}

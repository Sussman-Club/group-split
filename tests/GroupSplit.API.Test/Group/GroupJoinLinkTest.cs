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
/// The link a group shares, and what happens to whoever opens it.
/// </summary>
/// <remarks>
/// An invitation reaches an address and is only discoverable by signing in and looking, so
/// somebody invited before they have an account has no way to know. A link reaches the
/// thread where the group already talks. These are mostly about the three ways a link can
/// be dead -- unknown, expired, withdrawn -- because the person holding it has no other way
/// to find out which, and about it granting membership of exactly one group and no more.
/// </remarks>
public class GroupJoinLinkTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IJoinLinkService Links => GetService<IJoinLinkService>();
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "Lisbon") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    /// <summary>
    /// Everything the person following a link does happens as them. A scope of their own is
    /// how a test is somebody else for a moment.
    /// </summary>
    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    /// <summary>Moves a link's expiry into the past, which no API of ours can do.</summary>
    private async Task Expire(string token)
    {
        var link = await DbContext.Set<GroupJoinLink>().FirstAsync(candidate => candidate.Token == token, Ct);

        link.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await DbContext.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_group_starts_with_no_link()
    {
        var group = await AGroup();

        Assert.Empty(await Links.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task A_member_makes_a_link_and_can_read_it_back()
    {
        var group = await AGroup();

        var made = await Links.Create(group.Id, Ct);

        Assert.NotEmpty(made.Token);
        Assert.Equal(group.Id, made.GroupId);
        Assert.Equal("Lisbon", made.GroupName);
        Assert.True(made.ExpiresAt > DateTimeOffset.UtcNow);

        // Reading it again is the case the feature exists for: somebody comes back a week
        // later to paste the same link into another thread.
        var read = Assert.Single(await Links.ForGroup(group.Id, Ct));

        Assert.Equal(made.Token, read.Token);
    }

    [Fact]
    public async Task Two_links_are_not_the_same_token()
    {
        var first = await Links.Create((await AGroup("One")).Id, Ct);
        var second = await Links.Create((await AGroup("Two")).Id, Ct);

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task Making_a_second_link_puts_the_first_one_out()
    {
        var group = await AGroup();

        var first = await Links.Create(group.Id, Ct);
        var second = await Links.Create(group.Id, Ct);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(second.Token, Assert.Single(await Links.ForGroup(group.Id, Ct)).Token);

        // The old URL is still sitting in whichever thread it was pasted into, so what it
        // says when somebody opens it is the whole point of keeping the row.
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Links.Describe(first.Token, Ct));

        Assert.Equal(ErrorCodes.GroupJoinLinkRevoked, refusal.Code);
    }

    [Fact]
    public async Task A_link_for_a_group_the_caller_is_not_in_is_a_404()
    {
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() => Links.Create(Guid.NewGuid(), Ct));

        Assert.Equal(ErrorCodes.GroupNotFound, refusal.Code);
    }

    [Fact]
    public async Task A_token_nobody_issued_is_a_404()
    {
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Links.Describe("not-a-token-anybody-made", Ct));

        Assert.Equal(ErrorCodes.GroupJoinLinkNotFound, refusal.Code);
    }

    [Fact]
    public async Task A_revoked_link_says_it_was_withdrawn()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        await Links.Revoke(group.Id, Ct);

        Assert.Empty(await Links.ForGroup(group.Id, Ct));

        var described = await Assert.ThrowsAsync<ConflictException>(() => Links.Describe(link.Token, Ct));
        var accepted = await Assert.ThrowsAsync<ConflictException>(() => Links.Accept(link.Token, Ct));

        Assert.Equal(ErrorCodes.GroupJoinLinkRevoked, described.Code);
        Assert.Equal(ErrorCodes.GroupJoinLinkRevoked, accepted.Code);
    }

    [Fact]
    public async Task An_expired_link_says_it_expired_and_not_that_it_was_withdrawn()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        await Expire(link.Token);

        Assert.Empty(await Links.ForGroup(group.Id, Ct));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Links.Accept(link.Token, Ct));

        // Two things that can happen to a link, and the person holding it can act on the
        // difference: one is the group's doing and one is nobody's.
        Assert.Equal(ErrorCodes.GroupJoinLinkExpired, refusal.Code);
    }

    [Fact]
    public async Task Following_a_link_joins_the_group_it_names()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);
        var (scope, follower) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IJoinLinkService>();

            var described = await theirs.Describe(link.Token, Ct);

            Assert.Equal(group.Id, described.GroupId);
            Assert.Equal("Lisbon", described.GroupName);
            Assert.False(described.AlreadyAMember);

            var joined = await theirs.Accept(link.Token, Ct);

            Assert.Equal(group.Id, joined.GroupId);
            Assert.False(joined.AlreadyAMember);
        }

        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Contains(members, member => member.Id == follower.Id);
    }

    [Fact]
    public async Task A_link_grants_the_one_group_it_names_and_nothing_else()
    {
        var shared = await AGroup("Lisbon");
        var other = await AGroup("Flat");

        var link = await Links.Create(shared.Id, Ct);
        var (scope, follower) = await AnotherPerson();

        using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<IJoinLinkService>().Accept(link.Token, Ct);

            var theirGroups = await (await scope.ServiceProvider.GetRequiredService<IGroupService>()
                .GetAllGroups(Ct)).ToListAsync(Ct);

            var only = Assert.Single(theirGroups);
            Assert.Equal(shared.Id, only.Id);
        }

        var otherMembers = await (await Groups.GetGroupMembers(other.Id, Ct)).ToListAsync(Ct);

        Assert.DoesNotContain(otherMembers, member => member.Id == follower.Id);
    }

    [Fact]
    public async Task Following_it_stamps_when_they_joined()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);
        var (scope, follower) = await AnotherPerson();

        using (scope)
            await scope.ServiceProvider.GetRequiredService<IJoinLinkService>().Accept(link.Token, Ct);

        var membership = await DbContext.Set<GroupMembership>()
            .FirstAsync(row => row.GroupId == group.Id && row.UserId == follower.Id, Ct);

        Assert.NotEqual(default, membership.JoinedAt);
    }

    [Fact]
    public async Task Following_the_same_link_twice_joins_once_and_says_so()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);
        var (scope, follower) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IJoinLinkService>();

            await theirs.Accept(link.Token, Ct);

            // Opening a URL twice is the ordinary thing to do with one that lives in a chat
            // thread, so the second time is an answer rather than a refusal.
            var again = await theirs.Accept(link.Token, Ct);

            Assert.True(again.AlreadyAMember);
            Assert.Equal(group.Id, again.GroupId);

            Assert.True((await theirs.Describe(link.Token, Ct)).AlreadyAMember);
        }

        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Equal(2, members.Count);
        Assert.Single(members, member => member.Id == follower.Id);
    }

    [Fact]
    public async Task Somebody_already_in_the_group_is_told_so_rather_than_refused()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);

        // The member who made it, following it -- which is what happens when they check
        // that the link they pasted actually works.
        var joined = await Links.Accept(link.Token, Ct);

        Assert.True(joined.AlreadyAMember);
        Assert.Equal(group.Id, joined.GroupId);
    }

    /// <summary>
    /// Both ways in at once: named in an invitation, and joining by the group's open link
    /// before that invitation is ever claimed. The invitation stands.
    /// </summary>
    /// <remarks>
    /// It used to be closed here, because an invitation was an address and the joiner's
    /// address matched it. A link names nobody in particular now, so nothing has told the
    /// group that the person who walked in is the person it named -- and guessing from a
    /// name would hand a stranger's position to whoever the group happened to call Carlos.
    /// Withdrawing the invitation is how the group says so, and that hands the position over
    /// rather than dropping it.
    /// </remarks>
    [Fact]
    public async Task Joining_by_link_leaves_a_standing_invitation_alone()
    {
        var group = await AGroup();
        var link = await Links.Create(group.Id, Ct);
        var (scope, _) = await AnotherPerson();

        await Invitations.Invite(group.Id, new InviteToGroupRequest { Names = ["Carlos"] }, Ct);

        using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<IJoinLinkService>().Accept(link.Token, Ct);
        }

        Assert.Single(await Invitations.ForGroup(group.Id, Ct));
    }

    /// <summary>
    /// A link is a second way in, not a replacement: the group can still ask an address,
    /// and that invitation is still there to answer.
    /// </summary>
    [Fact]
    public async Task Naming_somebody_still_works_alongside_a_group_link()
    {
        var group = await AGroup();

        await Links.Create(group.Id, Ct);

        var pending = await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Carlos"] }, Ct);

        // Two doors, and they are different doors: the group's link lets anybody in as
        // themselves, and Carlos's link hands over the position recorded against his name.
        Assert.Equal("Carlos", Assert.Single(pending).Name);
        Assert.NotEmpty(await Links.ForGroup(group.Id, Ct));
    }
}

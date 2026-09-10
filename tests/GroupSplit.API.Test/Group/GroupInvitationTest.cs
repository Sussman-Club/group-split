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
/// Joining a group is something a person agrees to.
/// </summary>
/// <remarks>
/// It used to be something a group did to them: an address was looked up, and an account
/// with it was in the group from that moment -- while an address with no account behind it
/// was silently dropped and the person who typed it was told the member had been added.
/// <para>
/// It stopped being an address at all. A group names the person it is sharing costs with and
/// gets a link to send them; whoever opens that link and claims it becomes that person. So
/// the tests here are about tokens rather than about addresses. There is still a "my
/// invitations" list, and it answers a question it can actually ask: not the ones sent to
/// my address, but the ones whose link I have opened.
/// </para>
/// </remarks>
public class GroupInvitationTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "Lisbon") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    private static InviteToGroupRequest Asking(params string[] names) =>
        new() { Names = [.. names] };

    /// <summary>
    /// Everything the invitee does -- opening their link, claiming it -- happens as them. A
    /// scope of their own is how a test is somebody else for a moment.
    /// </summary>
    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    [Fact]
    public async Task Inviting_somebody_names_them_and_mints_a_link()
    {
        var group = await AGroup();

        var pending = await Invitations.Invite(group.Id, Asking("Carlos"), Ct);

        var invitation = Assert.Single(pending);

        Assert.Equal("Carlos", invitation.Name);
        Assert.Equal(group.Id, invitation.GroupId);
        Assert.NotEmpty(invitation.Token);

        // Nobody has joined: an invitation is not a membership. Asked of the membership
        // itself rather than of GetGroupMembers, which answers the wider question -- who the
        // group may record money against -- and does include them, marked as waiting.
        var joined = await DbContext.Set<GroupMembership>()
            .Where(membership => membership.GroupId == group.Id)
            .ToListAsync(Ct);

        Assert.Single(joined);

        var participants = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Equal(2, participants.Count);
        Assert.Contains(participants, person => person.Id == invitation.ParticipantUserId);
    }

    /// <summary>
    /// Two people really can be called Dani, and refusing the second would be the app
    /// telling a group it has misremembered its own friends.
    /// </summary>
    [Fact]
    public async Task Two_people_may_share_a_name()
    {
        var group = await AGroup();

        await Invitations.Invite(group.Id, Asking("Dani"), Ct);
        await Invitations.Invite(group.Id, Asking("Dani"), Ct);

        var pending = await Invitations.ForGroup(group.Id, Ct);

        Assert.Equal(2, pending.Count);

        // Two people, not one named twice: separate stand-ins, so a share given to one is
        // not a share given to the other.
        Assert.Equal(2, pending.Select(invitation => invitation.ParticipantUserId).Distinct().Count());
        Assert.Equal(2, pending.Select(invitation => invitation.Token).Distinct().Count());
    }

    [Fact]
    public async Task Inviting_several_people_at_once_makes_one_invitation_each()
    {
        var group = await AGroup();

        var pending = await Invitations.Invite(group.Id, Asking("Carlos", "Nuria", "Tomás"), Ct);

        Assert.Equal(3, pending.Count);
        Assert.Equal(3, pending.Select(invitation => invitation.Token).Distinct().Count());
    }

    [Fact]
    public async Task Inviting_nobody_says_so_rather_than_answering_with_the_group()
    {
        var group = await AGroup();

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Invitations.Invite(group.Id, Asking("   ", ""), Ct));

        Assert.Equal(ErrorCodes.GroupInvitationNoName, refusal.Code);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task Inviting_to_a_group_the_caller_is_not_in_is_a_404()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Invitations.Invite(Guid.NewGuid(), Asking("Carlos"), Ct));
    }

    [Fact]
    public async Task Claiming_puts_the_claimer_in_the_group_and_uses_the_link_up()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var (scope, claimer) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            var described = await theirs.Describe(invitation.Token, Ct);

            Assert.Equal(group.Id, described.GroupId);
            Assert.Equal("Carlos", described.Name);
            Assert.False(described.AlreadyAMember);

            var claimed = await theirs.Claim(invitation.Token, Ct);

            Assert.Equal(group.Id, claimed.GroupId);
            Assert.Equal("Carlos", claimed.Name);

            // Once. A forwarded copy of the link has nothing left to claim, and gets the
            // same answer a mistyped one does.
            var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
                theirs.Claim(invitation.Token, Ct));

            Assert.Equal(ErrorCodes.GroupInvitationNotFound, refusal.Code);
        }

        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Contains(members, member => member.Id == claimer.Id);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));

        // The stand-in goes with it: it existed to hold a position until somebody claimed
        // it, and somebody has.
        Assert.Null(await DbContext.Set<Data.Entities.User>()
            .FirstOrDefaultAsync(user => user.Id == invitation.ParticipantUserId, Ct));
    }

    [Fact]
    public async Task Claiming_stamps_when_they_joined()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var (scope, claimer) = await AnotherPerson();

        using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Claim(invitation.Token, Ct);
        }

        var membership = await DbContext.Set<GroupMembership>()
            .FirstAsync(row => row.GroupId == group.Id && row.UserId == claimer.Id, Ct);

        Assert.NotEqual(default, membership.JoinedAt);
    }

    [Fact]
    public async Task Declining_clears_the_invitation_and_joins_nobody()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            var closed = await theirs.Decline(invitation.Token, Ct);

            Assert.Equal(InvitationOutcome.Declined, closed.Outcome);
            Assert.Equal("Carlos", closed.Name);

            await Assert.ThrowsAsync<NotFoundException>(() => theirs.Describe(invitation.Token, Ct));
        }

        var joined = await DbContext.Set<GroupMembership>()
            .Where(membership => membership.GroupId == group.Id)
            .ToListAsync(Ct);

        Assert.Single(joined);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    /// <summary>
    /// Opening a link is remembered, so somebody who wanders off can be offered it again.
    /// </summary>
    /// <remarks>
    /// The hole this fills: open the link, sign in, go and look at something else without
    /// answering, and the app -- which had just met you -- could not put the invitation back
    /// in front of you. The only way back was the message the link arrived in.
    /// </remarks>
    [Fact]
    public async Task Opening_a_link_puts_it_in_the_openers_own_list()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            // Nothing until they open it. An invitation is not addressed to anybody.
            Assert.Empty(await theirs.Mine(Ct));

            await theirs.Describe(invitation.Token, Ct);

            var mine = Assert.Single(await theirs.Mine(Ct));

            Assert.Equal(invitation.Id, mine.Id);
            Assert.Equal("Carlos", mine.Name);
            Assert.Equal(group.Id, mine.GroupId);

            // The token is in it, which is the whole point: this is the way back to a link
            // whose message is gone.
            Assert.Equal(invitation.Token, mine.Token);

            // Reading it twice is not two invitations.
            await theirs.Describe(invitation.Token, Ct);
            Assert.Single(await theirs.Mine(Ct));
        }

        // And it is theirs alone -- opening a link tells nobody else anything.
        Assert.Empty(await Invitations.Mine(Ct));
    }

    [Fact]
    public async Task Answering_one_takes_it_out_of_the_list()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            await theirs.Describe(invitation.Token, Ct);
            Assert.Single(await theirs.Mine(Ct));

            await theirs.Claim(invitation.Token, Ct);

            // Not filtered out: the invitation is gone and the row cascaded with it, so
            // there is no state here that could disagree about whether it is still open.
            Assert.Empty(await theirs.Mine(Ct));
        }
    }

    [Fact]
    public async Task Withdrawing_one_takes_it_out_of_the_list_too()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            await theirs.Describe(invitation.Token, Ct);

            await Invitations.Withdraw(group.Id, invitation.Id, Ct);

            Assert.Empty(await theirs.Mine(Ct));
        }
    }

    /// <summary>
    /// A token nobody minted, and one that has been answered, are the same answer. The
    /// alternative is telling whoever holds a forwarded link that it used to be good.
    /// </summary>
    [Fact]
    public async Task A_token_that_names_nothing_is_a_404()
    {
        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Invitations.Describe("not-a-token", Ct));

        Assert.Equal(ErrorCodes.GroupInvitationNotFound, refusal.Code);
    }

    [Fact]
    public async Task Withdrawing_takes_the_invitation_back()
    {
        var group = await AGroup();
        var invitation = Assert.Single(await Invitations.Invite(group.Id, Asking("Carlos"), Ct));

        var closed = await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Equal(InvitationOutcome.Withdrawn, closed.Outcome);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));

        // And the link with it.
        await Assert.ThrowsAsync<NotFoundException>(() => Invitations.Describe(invitation.Token, Ct));
    }

    [Fact]
    public async Task Withdrawing_one_that_is_not_there_is_a_404()
    {
        var group = await AGroup();

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Invitations.Withdraw(group.Id, Guid.NewGuid(), Ct));

        Assert.Equal(ErrorCodes.GroupInvitationNotFound, refusal.Code);
    }

    /// <summary>
    /// The links are in the rows, and a link is a claim on a position in this group's
    /// ledger -- so who may read the list is not a matter of tidiness.
    /// </summary>
    [Fact]
    public async Task The_group_only_shows_its_own_invitations_to_its_own_members()
    {
        var group = await AGroup();
        await Invitations.Invite(group.Id, Asking("Carlos"), Ct);

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            Assert.Empty(await theirs.ForGroup(group.Id, Ct));
        }
    }
}

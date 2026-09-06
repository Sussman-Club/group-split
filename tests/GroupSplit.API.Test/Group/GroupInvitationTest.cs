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
/// The second half is what these are mostly about, because inviting somebody who has not
/// signed up yet is the ordinary case for a new group and it did nothing at all.
/// </remarks>
public class GroupInvitationTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "Lisbon") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    private static AddMemberRequest Asking(params string[] emails) =>
        new([.. emails.Select(email => new UserIdentifier { Email = email })]);

    /// <summary>
    /// Everything the invitee does -- reading their invitations, accepting one -- happens as
    /// them. A scope of their own is how a test is somebody else for a moment.
    /// </summary>
    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    [Fact]
    public async Task An_address_with_no_account_behind_it_is_still_invited()
    {
        var group = await AGroup();

        var pending = await Invitations.Invite(group.Id, Asking("nobody@example.com"), Ct);

        var invitation = Assert.Single(pending);

        Assert.Equal("nobody@example.com", invitation.Email);
        Assert.Equal(group.Id, invitation.GroupId);

        // And nobody has joined: an invitation is not a membership.
        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);
        Assert.Single(members);
    }

    [Fact]
    public async Task Addresses_are_matched_without_regard_to_case()
    {
        var group = await AGroup();

        await Invitations.Invite(group.Id, Asking("Someone@Example.com"), Ct);
        await Invitations.Invite(group.Id, Asking("someone@example.COM"), Ct);

        var pending = await Invitations.ForGroup(group.Id, Ct);

        Assert.Single(pending);
        Assert.Equal("someone@example.com", pending[0].Email);
    }

    [Fact]
    public async Task Inviting_somebody_already_in_the_group_is_not_an_error_and_adds_nothing()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        var pending = await Invitations.Invite(group.Id, Asking(me.Email!, "new@example.com"), Ct);

        // The one who is already there is skipped; the other four of five would otherwise
        // fail for the sake of one.
        Assert.Single(pending);
        Assert.Equal("new@example.com", pending[0].Email);
    }

    [Fact]
    public async Task Inviting_to_a_group_the_caller_is_not_in_is_a_404()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Invitations.Invite(Guid.NewGuid(), Asking("someone@example.com"), Ct));
    }

    [Fact]
    public async Task Accepting_puts_the_invitee_in_the_group_and_clears_the_invitation()
    {
        var group = await AGroup();
        var (scope, invitee) = await AnotherPerson();

        using (scope)
        {
            await Invitations.Invite(group.Id, Asking(invitee.Email!), Ct);

            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            var mine = await theirs.Mine(Ct);
            var invitation = Assert.Single(mine);
            Assert.Equal(group.Id, invitation.GroupId);

            var joined = await theirs.Accept(invitation.Id, Ct);

            Assert.Equal(group.Id, joined.Id);
        }

        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);
        Assert.Contains(members, member => member.Id == invitee.Id);

        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task Joining_stamps_when_they_joined()
    {
        var group = await AGroup();
        var (scope, invitee) = await AnotherPerson();

        using (scope)
        {
            await Invitations.Invite(group.Id, Asking(invitee.Email!), Ct);

            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();
            var invitation = Assert.Single(await theirs.Mine(Ct));

            await theirs.Accept(invitation.Id, Ct);
        }

        var membership = await DbContext.Set<GroupMembership>()
            .FirstAsync(row => row.GroupId == group.Id && row.UserId == invitee.Id, Ct);

        Assert.NotEqual(default, membership.JoinedAt);
    }

    [Fact]
    public async Task Declining_clears_the_invitation_and_joins_nobody()
    {
        var group = await AGroup();
        var (scope, invitee) = await AnotherPerson();

        using (scope)
        {
            await Invitations.Invite(group.Id, Asking(invitee.Email!), Ct);

            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();
            var invitation = Assert.Single(await theirs.Mine(Ct));

            await theirs.Decline(invitation.Id, Ct);

            Assert.Empty(await theirs.Mine(Ct));
        }

        var members = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Single(members);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task An_invitation_addressed_to_somebody_else_cannot_be_accepted()
    {
        var group = await AGroup();
        var (scope, _) = await AnotherPerson();

        var pending = await Invitations.Invite(group.Id, Asking("stranger@example.com"), Ct);
        var invitation = Assert.Single(pending);

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            // A 403 rather than a 404: pretending it does not exist would be a lie the
            // invitee's own client could catch out by trying twice.
            var refusal = await Assert.ThrowsAsync<ForbiddenException>(() => theirs.Accept(invitation.Id, Ct));

            Assert.Equal(ErrorCodes.GroupInvitationNotYours, refusal.Code);
        }
    }

    [Fact]
    public async Task Withdrawing_takes_the_invitation_back()
    {
        var group = await AGroup();

        var pending = await Invitations.Invite(group.Id, Asking("someone@example.com"), Ct);
        var invitation = Assert.Single(pending);

        await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
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
    /// The case the old behaviour could not serve at all: somebody is invited before they
    /// have an account, and the invitation is waiting when they make one.
    /// </summary>
    [Fact]
    public async Task An_invitation_sent_before_the_account_existed_is_waiting_afterwards()
    {
        var group = await AGroup();

        // The address the next person to sign up will have. Nothing about the invitation
        // knows that yet -- it is matched by address when they read it, which is what makes
        // inviting somebody who has not signed up work at all.
        var (scope, invitee) = await AnotherPerson();

        using (scope)
        {
            await Invitations.Invite(group.Id, Asking(invitee.Email!), Ct);

            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            Assert.Single(await theirs.Mine(Ct));
        }
    }

    [Fact]
    public async Task The_group_only_shows_its_own_invitations_to_its_own_members()
    {
        var group = await AGroup();
        await Invitations.Invite(group.Id, Asking("someone@example.com"), Ct);

        var (scope, _) = await AnotherPerson();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            Assert.Empty(await theirs.ForGroup(group.Id, Ct));
        }
    }
}

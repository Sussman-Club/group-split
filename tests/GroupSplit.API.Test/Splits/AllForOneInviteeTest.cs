using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// The rule a group holds for each of its guests: all of it is for them, the same as every
/// member has.
/// </summary>
/// <remarks>
/// A participant can pay, hold a share and be named by a rule whether they have joined yet
/// or not, and the person most likely to need "all of it is theirs" is the friend invited
/// for the trip. They used to have no such rule until they joined, which the expense dialog
/// could only explain. Now they have one from the moment they are named, and what becomes of
/// it follows their position: theirs on a claim, retired when it lands on somebody who
/// already has one.
/// </remarks>
public class AllForOneInviteeTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupService Groups => GetService<IGroupService>();
    private IInvitationService Invitations => GetService<IInvitationService>();
    private ITransactionService Transactions => GetService<ITransactionService>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    private async Task<List<SplitRule>> RulesOf(Guid groupId)
    {
        // A scope of its own, so what this test's context is tracking cannot answer for the
        // database.
        using var scope = GetService<IServiceScopeFactory>().CreateScope();

        return await scope.ServiceProvider.GetRequiredService<Data.AppDbContext>().Set<SplitRule>()
            .Include(rule => rule.Versions)
            .Where(rule => rule.Group.Id == groupId)
            .ToListAsync(Ct);
    }

    private async Task<SplitRule?> BuiltInFor(Guid groupId, Guid userId) =>
        (await RulesOf(groupId)).FirstOrDefault(rule => rule.BuiltIn && rule.AllFor == userId);

    private async Task<(Data.Entities.Group Group, GroupInvitationResponse Invitation)> AGroupWaitingOn(string name)
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);
        var invitation = Assert.Single(await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = [name] }, Ct));

        return (group, invitation);
    }

    /// <summary>An expense of 120 that the invitee's own rule puts entirely on them.</summary>
    private async Task<Guid> AllForTheInvitee(Guid groupId, Guid standIn)
    {
        var rule = await BuiltInFor(groupId, standIn);
        Assert.NotNull(rule);

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Her flight",
            Amount = 120m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            SplitRuleId = rule.Id
        }, Ct);

        return expense.Id;
    }

    private async Task<(IServiceScope Scope, Data.Entities.User User)> AnotherPerson()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }

    [Fact]
    public async Task Inviting_somebody_gives_the_group_a_rule_that_puts_the_whole_amount_on_them()
    {
        var (group, invitation) = await AGroupWaitingOn("Luz");

        var rule = await BuiltInFor(group.Id, invitation.ParticipantUserId);

        Assert.NotNull(rule);
        Assert.Equal("All for Luz", rule.Name);

        var version = Assert.IsType<SoleSplitRuleVersion>(Assert.Single(rule.Versions));
        Assert.Equal(invitation.ParticipantUserId, version.UserId);
        Assert.Null(version.SupersededAt);
    }

    [Fact]
    public async Task Two_invitees_who_share_a_name_get_rules_the_group_can_tell_apart()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);

        var invited = await Invitations.Invite(group.Id, new InviteToGroupRequest { Names = ["Dani", "Dani"] }, Ct);

        Assert.Equal(2, invited.Count);

        var names = new List<string>();

        foreach (var invitation in invited)
            names.Add((await BuiltInFor(group.Id, invitation.ParticipantUserId))!.Name);

        Assert.Equal(["All for Dani", "All for Dani (2)"], names.Order());
    }

    [Fact]
    public async Task An_expense_that_is_all_for_an_invitee_divides_to_their_stand_in()
    {
        var (group, invitation) = await AGroupWaitingOn("Luz");

        var expenseId = await AllForTheInvitee(group.Id, invitation.ParticipantUserId);

        var split = Assert.Single(await DbContext.Set<TransactionSplit>().AsNoTracking()
            .Where(candidate => candidate.TransactionId == expenseId).ToListAsync(Ct));

        Assert.Equal(invitation.ParticipantUserId, split.UserId);
        Assert.Equal(120m, split.Amount);

        var balances = await (await Groups.GetGroupNetBalance(group.Id, Ct)).ToListAsync(Ct);
        Assert.Equal(0m, balances.Sum(balance => balance.Balance));
    }

    [Fact]
    public async Task Claiming_makes_the_invitees_rule_theirs_and_joining_does_not_add_a_second()
    {
        var (group, invitation) = await AGroupWaitingOn("Luz");
        var before = await BuiltInFor(group.Id, invitation.ParticipantUserId);
        var expenseId = await AllForTheInvitee(group.Id, invitation.ParticipantUserId);

        var (scope, luz) = await AnotherPerson();

        using (scope)
            await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(invitation.Token, Ct);

        var hers = (await RulesOf(group.Id)).Where(rule => rule.BuiltIn && rule.AllFor == luz.Id).ToList();

        var rule = Assert.Single(hers);
        Assert.Equal(before!.Id, rule.Id);
        Assert.Equal(before.Versions.Single().Id, rule.Versions.Single().Id);

        // The expense recorded before she joined still points at that version, and now says her.
        var expense = await DbContext.Set<Expense>().AsNoTracking()
            .Include(candidate => candidate.Splits)
            .FirstAsync(candidate => candidate.Id == expenseId, Ct);

        Assert.Equal(rule.Versions.Single().Id, expense.SplitRuleVersionId);
        Assert.Equal(luz.Id, Assert.Single(expense.Splits).UserId);

        // The stand-in stays, holding nothing: it is what the history names for the time
        // before the claim.
        Assert.True(await DbContext.Set<Data.Entities.User>()
            .AnyAsync(user => user.Id == invitation.ParticipantUserId, Ct));
    }

    [Fact]
    public async Task Claiming_by_somebody_already_in_the_group_retires_the_invitees_unused_rule()
    {
        var (group, invitation) = await AGroupWaitingOn("Ana again");
        var theirs = await BuiltInFor(group.Id, invitation.ParticipantUserId);
        var mine = await BuiltInFor(group.Id, Self);

        await Invitations.Claim(invitation.Token, Ct);

        var rules = await RulesOf(group.Id);

        Assert.DoesNotContain(rules, rule => rule.Id == theirs!.Id);
        Assert.Single(rules, rule => rule.BuiltIn && rule.AllFor == Self);
        Assert.Contains(rules, rule => rule.Id == mine!.Id);
    }

    [Fact]
    public async Task Claiming_by_somebody_already_in_the_group_keeps_a_used_rule_but_hides_it_from_the_picker()
    {
        var (group, invitation) = await AGroupWaitingOn("Ana again");
        var theirs = await BuiltInFor(group.Id, invitation.ParticipantUserId);
        var expenseId = await AllForTheInvitee(group.Id, invitation.ParticipantUserId);

        await Invitations.Claim(invitation.Token, Ct);

        var rules = await RulesOf(group.Id);

        // Kept, because an expense was divided by it, and no longer one the group was given:
        // the member already has their own, and two would be two "One pays" for one person.
        var kept = Assert.Single(rules, rule => rule.Id == theirs!.Id);
        Assert.False(kept.BuiltIn);
        Assert.Equal(Self, kept.AllFor);

        Assert.Single(rules, rule => rule.BuiltIn && rule.AllFor == Self);

        var expense = await DbContext.Set<Expense>().AsNoTracking()
            .Include(candidate => candidate.Splits)
            .FirstAsync(candidate => candidate.Id == expenseId, Ct);

        Assert.Equal(Self, Assert.Single(expense.Splits).UserId);
        Assert.Equal(120m, expense.Splits.Single().Amount);
    }

    /// <summary>
    /// Turning a link down or stopping it is about the link: the guest, and the rule that is
    /// theirs, both stay.
    /// </summary>
    [Fact]
    public async Task Declining_the_link_leaves_the_guests_rule_theirs()
    {
        var (group, invitation) = await AGroupWaitingOn("Luz");
        var theirs = await BuiltInFor(group.Id, invitation.ParticipantUserId);
        await AllForTheInvitee(group.Id, invitation.ParticipantUserId);

        await Invitations.Decline(invitation.Token, Ct);

        var kept = Assert.Single(await RulesOf(group.Id), rule => rule.Id == theirs!.Id);

        Assert.True(kept.BuiltIn);
        Assert.Equal(invitation.ParticipantUserId, kept.AllFor);
    }

    [Fact]
    public async Task Withdrawing_the_link_leaves_the_guests_rule_theirs()
    {
        var (group, invitation) = await AGroupWaitingOn("Luz");
        var theirs = await BuiltInFor(group.Id, invitation.ParticipantUserId);

        await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Contains(await RulesOf(group.Id), rule => rule.Id == theirs!.Id && rule.BuiltIn);
    }

    /// <summary>
    /// A removed guest keeps their rule the way a member who leaves does: naming the
    /// stand-in that is still there, and no longer dividing anything new.
    /// </summary>
    [Fact]
    public async Task Removing_a_guest_keeps_their_rule_like_a_leaver()
    {
        var (group, invitation) = await AGroupWaitingOn("Luz");

        await Groups.RemoveGroupMember(group.Id, invitation.ParticipantUserId, Ct);

        Assert.NotNull(await BuiltInFor(group.Id, invitation.ParticipantUserId));
    }

    /// <summary>
    /// Nothing about departures changed with this: a member's rule stays when they leave,
    /// naming the account that is still there, and simply stops dividing.
    /// </summary>
    [Fact]
    public async Task A_member_leaving_still_keeps_their_rule()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);
        var (scope, leaver) = await AnotherPerson();

        using (scope)
        {
            await JoinGroup(group.Id, leaver);
            await GetService<IMemberSplitRules>().EnsureFor(
                await DbContext.Set<Data.Entities.Group>().FirstAsync(candidate => candidate.Id == group.Id, Ct),
                await DbContext.Set<Data.Entities.User>().FirstAsync(candidate => candidate.Id == leaver.Id, Ct), Ct);

            await scope.ServiceProvider.GetRequiredService<IGroupService>().Leave(group.Id, Ct);
        }

        var theirs = await BuiltInFor(group.Id, leaver.Id);

        Assert.NotNull(theirs);
    }
}

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
/// Somebody who has been invited and has not answered is a participant in the group's
/// money: they can be given a share, they can be the payer, and their balance is in the
/// group's column.
/// </summary>
/// <remarks>
/// Inviting somebody is the moment a group starts, and it is exactly the moment there is a
/// backlog of expenses to enter -- the trip that was just booked, the flat that was just
/// moved into. Until this, none of that could name them: the group had to wait for
/// everybody to sign up, or record the shares wrong and fix them one by one later.
/// <para>
/// The invariant every test here is really about is the one the whole ledger rests on: each
/// transaction's shares sum to its amount, and the group's balances sum to zero. Adding a
/// kind of participant is the sort of change that breaks it quietly, and nothing else
/// catches it -- so most of these check the arithmetic as well as the behaviour.
/// </para>
/// </remarks>
public class PendingInviteeTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private ISplitRuleService Rules => GetService<ISplitRuleService>();
    private ISettlementService Settlements => GetService<ISettlementService>();
    private IGroupParticipants Participants => GetService<IGroupParticipants>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "The flat") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    private static InviteToGroupRequest Asking(params string[] names) =>
        new() { Names = [.. names] };

    /// <summary>
    /// Invites one person and hands back the invitation, which is what a caller needs: its
    /// id to withdraw it with, its token to answer it with, and the participant's id to give
    /// a share to.
    /// </summary>
    private async Task<GroupInvitationResponse> Invite(Guid groupId, string name)
    {
        var pending = await Invitations.Invite(groupId, Asking(name), Ct);

        return pending.Single(invitation => invitation.Name == name);
    }

    private Task<Data.Entities.Expense> AnExpense(Guid groupId, decimal amount,
        Guid? payer = null, IReadOnlyList<SplitInput>? splits = null, Guid? category = null) =>
        Transactions.Create(new CreateTransactionRequest
        {
            GroupId = groupId,
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            Name = "Rent",
            CategoryId = category,
            PaidByUserId = payer,
            Splits = splits?.ToList()
        }, Ct).AsTask();

    /// <summary>
    /// The group's balances, and the check that they add up. A column of balances summing
    /// to anything but zero means money belonging to nobody on the page.
    /// </summary>
    private async Task<IReadOnlyList<GroupNetBalance>> Balances(Guid groupId)
    {
        var balances = await (await Groups.GetGroupNetBalance(groupId, Ct)).ToListAsync(Ct);

        Assert.Equal(0m, balances.Sum(balance => balance.Balance));

        return balances;
    }

    /// <summary>Every transaction in the group divides into exactly its own amount.</summary>
    /// <remarks>
    /// Untracked, and that matters here rather than being a habit: a claim happens in a
    /// scope of its own, so this context may already be holding the split rows as they were
    /// before it, and EF hands back what it is tracking rather than what the store now says.
    /// </remarks>
    private async Task AssertSharesReconcile(Guid groupId)
    {
        var transactions = await DbContext.Set<Data.Entities.Transaction>()
            .AsNoTracking()
            .Where(transaction => transaction.GroupId == groupId)
            .Include(transaction => transaction.Splits)
            .ToListAsync(Ct);

        Assert.NotEmpty(transactions);

        foreach (var transaction in transactions)
        {
            Assert.Equal(transaction.Amount, transaction.Splits.Sum(split => split.Amount));
        }
    }

    // ---- Being choosable ---------------------------------------------------------------

    [Fact]
    public async Task An_invited_address_is_somebody_the_group_can_give_a_share_to()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 90m, splits:
        [
            new SplitInput { UserId = TestUser().Id, Amount = 60m },
            new SplitInput { UserId = invitation.ParticipantUserId, Amount = 30m }
        ]);

        var stored = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, stored.Count);
        Assert.Equal(30m, stored.Single(split => split.UserId == invitation.ParticipantUserId).Amount);
        Assert.Equal(90m, stored.Sum(split => split.Amount));
    }

    /// <summary>
    /// Nothing has to name them for them to be included: an even division is between the
    /// group's participants, and they are one.
    /// </summary>
    [Fact]
    public async Task An_even_split_divides_between_the_invited_as_well_as_the_joined()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 90m);

        var stored = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, stored.Count);
        Assert.Equal(45m, stored.Single(split => split.UserId == invitation.ParticipantUserId).Amount);
    }

    [Fact]
    public async Task An_invitee_can_be_the_payer_of_an_expense()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 80m, payer: invitation.ParticipantUserId);

        Assert.Equal(invitation.ParticipantUserId, expense.UserId);

        var balances = await Balances(group.Id);

        // They fronted it and owe half of it, so the group owes them the other half.
        Assert.Equal(40m, balances.Single(b => b.UserId == invitation.ParticipantUserId).Balance);
    }

    [Fact]
    public async Task A_split_rule_can_name_an_invitee()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Two thirds mine",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int>
                {
                    [TestUser().Id] = 2,
                    [invitation.ParticipantUserId] = 1
                }
            }
        }, Ct);

        var named = await DbContext.Set<SplitRuleParticipant>()
            .Where(participant => participant.SplitRuleId == rule.Id)
            .ToListAsync(Ct);

        Assert.Contains(named, participant => participant.UserId == invitation.ParticipantUserId);
    }

    [Fact]
    public async Task A_rule_still_cannot_name_somebody_the_group_has_never_heard_of()
    {
        var group = await AGroup();
        var stranger = await CreateNewUser();

        var refused = await Assert.ThrowsAsync<ValidationException>(() => Rules.Create(
            new CreateSplitRuleRequest
            {
                GroupId = group.Id,
                Name = "Odd one",
                Definition = new SharesSplitRuleDto
                {
                    Shares = new Dictionary<Guid, int> { [stranger.Id] = 1 }
                }
            }, Ct));

        Assert.Equal(ErrorCodes.RuleUsersNotInGroup, refused.Code);
    }

    // ---- Being visible ----------------------------------------------------------------

    [Fact]
    public async Task The_members_listing_says_which_of_them_have_joined()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var people = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Equal(2, people.Count);

        var waiting = people.Single(person => person.Id == invitation.ParticipantUserId);

        Assert.True(await Participants.IsPendingInvitee(group.Id, waiting.Id, Ct));
        Assert.False(await Participants.IsPendingInvitee(group.Id, TestUser().Id, Ct));

        // The name the group gave them, and no address: nobody has signed in as this
        // person, so there is no profile to read one out of.
        Assert.Equal("Carlos", waiting.FirstName);
        Assert.Null(waiting.Email);
    }

    [Fact]
    public async Task Their_balance_is_in_the_group_and_marked_as_not_having_joined()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var balances = await Balances(group.Id);

        var theirs = balances.Single(balance => balance.UserId == invitation.ParticipantUserId);

        Assert.True(theirs.IsPendingInvitee);
        Assert.Equal(-45m, theirs.Balance);
        Assert.Equal("Carlos", theirs.UserName);

        Assert.False(balances.Single(balance => balance.UserId == TestUser().Id).IsPendingInvitee);
    }

    // ---- Nobody to settle with --------------------------------------------------------

    /// <summary>
    /// Their row is in the balances and out of the plan, and both are deliberate: the
    /// column has to add up, and a plan is a list of payments somebody can actually make.
    /// </summary>
    [Fact]
    public async Task The_settlement_plan_leaves_them_out_while_the_balances_keep_them()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var balances = await Balances(group.Id);
        var position = await GetService<IDebtCalculationService>().GetUserBalance(balances);

        Assert.Contains(position.NetBalances,
            balance => balance.UserId == invitation.ParticipantUserId);

        Assert.DoesNotContain(position.Plan,
            payment => payment.FromUserId == invitation.ParticipantUserId ||
                       payment.ToUserId == invitation.ParticipantUserId);

        Assert.Empty(position.OwedToYou);
        Assert.Empty(position.YouOwed);
    }

    [Fact]
    public async Task Settling_up_with_one_of_them_is_refused_by_name()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Groups.Settle(group.Id,
            new SettleRequest { UserId = invitation.ParticipantUserId, Amount = 45m }, Ct));

        Assert.Equal(ErrorCodes.SettlementWithPendingInvitee, refused.Code);
    }

    [Fact]
    public async Task Recording_a_repayment_that_names_one_of_them_is_refused_too()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements.RecordRepayment(group.Id, new RecordRepaymentRequest
            {
                FromUserId = invitation.ParticipantUserId,
                ToUserId = TestUser().Id,
                Amount = 10m
            }, Ct));

        Assert.Equal(ErrorCodes.SettlementWithPendingInvitee, refused.Code);
    }

    [Fact]
    public async Task Removing_one_of_them_as_a_member_says_to_withdraw_the_invitation()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Groups.RemoveGroupMember(group.Id, invitation.ParticipantUserId, Ct));

        Assert.Equal(ErrorCodes.GroupMemberNotJoined, refused.Code);
    }

    // ---- Accepting --------------------------------------------------------------------

    /// <summary>
    /// Claiming changes no amount and asks nothing of anybody: the position simply stops
    /// being a stand-in's and starts being an account's.
    /// </summary>
    /// <remarks>
    /// This is the criterion the whole design exists to meet, and where a link differs from
    /// an address. With an address, the account that accepted already <em>was</em> the
    /// participant, so accepting moved nothing at all. A link names nobody in particular, so
    /// claiming is the moment the shares change hands -- one re-pointing, no arithmetic, and
    /// the group's balances read exactly the same either side of it.
    /// </remarks>
    [Fact]
    public async Task Claiming_makes_the_history_theirs_without_changing_anything()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);
        await AnExpense(group.Id, 40m, payer: invitation.ParticipantUserId);

        var before = await Balances(group.Id);
        var theirs = before.Single(balance => balance.UserId == invitation.ParticipantUserId);

        var (scope, carlos) = await Signing();

        InvitationClaimedResponse claimed;

        using (scope)
        {
            claimed = await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Claim(invitation.Token, Ct);
        }

        Assert.Equal(2, claimed.SharesTaken);
        Assert.Equal(65m, claimed.AmountOwed);
        Assert.Equal(1, claimed.PaymentsTaken);
        Assert.Equal(40m, claimed.AmountPaid);

        var after = await Balances(group.Id);
        var mine = after.Single(balance => balance.UserId == carlos.Id);

        // The same position, to the cent, under a different name.
        Assert.Equal(theirs.Balance, mine.Balance);
        Assert.Equal(theirs.AmountOwed, mine.AmountOwed);
        Assert.Equal(theirs.AmountPaid, mine.AmountPaid);

        // A member now, and no longer waiting.
        Assert.False(mine.IsPendingInvitee);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));

        await AssertSharesReconcile(group.Id);
    }

    /// <summary>
    /// Claiming when the claimer already had a share of the same expense: two rows become
    /// one, because a transaction may hold only one opinion about what a person owed.
    /// </summary>
    [Fact]
    public async Task Claiming_adds_to_a_share_the_claimer_already_had()
    {
        var group = await AGroup();

        var (scope, carlos) = await Signing();

        using (scope)
        {
            await JoinGroup(group.Id, carlos);

            var invitation = await Invite(group.Id, "Carlos");

            var expense = await AnExpense(group.Id, 90m, splits:
            [
                new SplitInput { UserId = TestUser().Id, Amount = 30m },
                new SplitInput { UserId = carlos.Id, Amount = 30m },
                new SplitInput { UserId = invitation.ParticipantUserId, Amount = 30m }
            ]);

            await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Claim(invitation.Token, Ct);

            // Untracked: the claim ran in its own scope, and this context is still holding
            // the rows as they were before it.
            var stored = await DbContext.Set<TransactionSplit>()
                .AsNoTracking()
                .Where(split => split.TransactionId == expense.Id)
                .ToListAsync(Ct);

            Assert.Equal(2, stored.Count);
            Assert.Equal(60m, stored.Single(split => split.UserId == carlos.Id).Amount);
            Assert.Equal(90m, stored.Sum(split => split.Amount));
        }

        await AssertSharesReconcile(group.Id);
        await Balances(group.Id);
    }

    /// <summary>
    /// Following a group's open join link is not claiming an invitation, and does not
    /// answer one.
    /// </summary>
    /// <remarks>
    /// It cannot: a join link lets somebody in as themselves, and the group is still waiting
    /// on the person it named -- nothing has told it the two are the same, and guessing from
    /// a name would be handing a stranger's ledger to whoever the group happened to call
    /// Carlos. Withdrawing the invitation is how the group says so, and that hands the
    /// position over rather than dropping it.
    /// </remarks>
    [Fact]
    public async Task Joining_by_the_group_link_leaves_the_invitation_standing()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var (scope, carlos) = await Signing();

        using (scope)
        {
            var joiner = scope.ServiceProvider.GetRequiredService<IGroupJoiner>();
            var context = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();

            var tracked = await context.Set<Data.Entities.Group>()
                .Include(candidate => candidate.Users)
                .FirstAsync(candidate => candidate.Id == group.Id, Ct);

            Assert.True(await joiner.Join(tracked, carlos, Ct));
        }

        Assert.Single(await Invitations.ForGroup(group.Id, Ct));
        Assert.True(await Participants.IsPendingInvitee(group.Id, invitation.ParticipantUserId, Ct));

        var people = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        // Three participants: two members and the person still being waited on.
        Assert.Equal(3, people.Count);
    }

    // ---- Declining and withdrawing ----------------------------------------------------

    [Fact]
    public async Task Withdrawing_with_nothing_recorded_says_so_and_moves_nothing()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var closed = await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Equal(InvitationOutcome.Withdrawn, closed.Outcome);
        Assert.Equal("Carlos", closed.Name);
        Assert.False(closed.MovedAnything);
        Assert.Null(closed.AbsorbedByUserId);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    /// <summary>
    /// The case the issue says needs deciding rather than defaulting. The decision: it goes
    /// to the member who invited them, in full, and the answer says so.
    /// </summary>
    [Fact]
    public async Task Withdrawing_hands_what_was_recorded_to_the_member_who_invited_them()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        // Ninety split evenly, and forty they fronted themselves.
        await AnExpense(group.Id, 90m);
        await AnExpense(group.Id, 40m, payer: invitation.ParticipantUserId);

        var closed = await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.True(closed.MovedAnything);
        Assert.Equal(TestUser().Id, closed.AbsorbedByUserId);
        Assert.Equal(2, closed.SharesMoved);
        Assert.Equal(65m, closed.AmountOwed);
        Assert.Equal(1, closed.PaymentsMoved);
        Assert.Equal(40m, closed.AmountPaid);

        // Nothing was re-divided: both expenses still divide into exactly their own
        // amounts, and the group -- now one person -- is square with itself.
        await AssertSharesReconcile(group.Id);

        var balances = await Balances(group.Id);

        Assert.Single(balances);
        Assert.Equal(0m, balances[0].Balance);
        Assert.Equal(130m, balances[0].AmountPaid);
        Assert.Equal(130m, balances[0].AmountOwed);
    }

    /// <summary>
    /// Two shares on one transaction become one row, because a transaction may hold only
    /// one opinion about what a person owed -- and the unique index says so.
    /// </summary>
    [Fact]
    public async Task Shares_on_the_same_expense_are_added_together_rather_than_doubled()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 90m);

        await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        var stored = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Single(stored);
        Assert.Equal(90m, stored[0].Amount);
        Assert.Equal(TestUser().Id, stored[0].UserId);
    }

    [Fact]
    public async Task Withdrawing_takes_them_out_of_the_rules_that_named_them()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Even between us",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int>
                {
                    [TestUser().Id] = 1,
                    [invitation.ParticipantUserId] = 1
                }
            }
        }, Ct);

        var closed = await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Equal(1, closed.RulesAffected);

        var named = await DbContext.Set<SplitRuleParticipant>()
            .Where(participant => participant.SplitRuleId == rule.Id)
            .ToListAsync(Ct);

        Assert.Single(named);
        Assert.Equal(TestUser().Id, named[0].UserId);
    }

    [Fact]
    public async Task Declining_moves_the_money_the_same_way_and_says_who_has_it_now()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var (scope, _) = await Signing();

        InvitationClosedResponse closed;

        using (scope)
        {
            closed = await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Decline(invitation.Token, Ct);
        }

        Assert.Equal(InvitationOutcome.Declined, closed.Outcome);
        Assert.Equal("Carlos", closed.Name);
        Assert.Equal(TestUser().Id, closed.AbsorbedByUserId);
        Assert.Equal(45m, closed.AmountOwed);

        await AssertSharesReconcile(group.Id);

        var balances = await Balances(group.Id);

        Assert.Single(balances);
        Assert.Equal(0m, balances[0].Balance);
    }

    /// <summary>
    /// The inviter can be gone by the time somebody answers. Then it is the group's
    /// longest-standing member, which is deterministic and always somebody.
    /// </summary>
    [Fact]
    public async Task When_the_inviter_has_left_the_longest_standing_member_takes_it_on()
    {
        var group = await AGroup();
        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        var invitation = await Invite(group.Id, "Carlos");

        await AnExpense(group.Id, 90m, splits:
        [
            new SplitInput { UserId = other.Id, Amount = 60m },
            new SplitInput { UserId = invitation.ParticipantUserId, Amount = 30m }
        ]);

        // The inviter goes. They paid for the expense and owe none of it, so their balance
        // is not settled -- so they are detached directly, the way an account deletion
        // does it, rather than through the endpoint that refuses.
        var tracked = await DbContext.Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == group.Id, Ct);

        var inviter = await DbContext.Set<Data.Entities.User>()
            .FirstAsync(user => user.Id == TestUser().Id, Ct);

        await Groups.DetachMember(tracked, inviter, Ct);
        await DbContext.SaveChangesAsync(Ct);

        // Withdrawn by the member who is left, because withdrawing is a thing a member of
        // the group does and the inviter is no longer one.
        using var theirs = AsMember(other.Id);

        var closed = await theirs.ServiceProvider.GetRequiredService<IInvitationService>()
            .Withdraw(group.Id, invitation.Id, Ct);

        Assert.Equal(other.Id, closed.AbsorbedByUserId);
        Assert.Equal(30m, closed.AmountOwed);

        await AssertSharesReconcile(group.Id);
    }

    /// <summary>
    /// Deleting an account cannot strand a group's money in an invitation, because no
    /// pending invitation can name an account.
    /// </summary>
    /// <remarks>
    /// It could when invitations were addresses: a group recording shares against an address
    /// that already had an account held a position in that account's name, in a group it had
    /// never joined -- which the settled-up check could not see, since that asks about the
    /// groups somebody is a member of. An invitation holds a stand-in of its own now, and
    /// claiming is what attaches an account to it, so the two cannot meet.
    /// </remarks>
    [Fact]
    public async Task An_invitation_never_holds_a_real_accounts_money()
    {
        var group = await AGroup();

        var (scope, carlos) = await Signing();

        using (scope)
        {
            var invitation = await Invite(group.Id, "Carlos");

            Assert.NotEqual(carlos.Id, invitation.ParticipantUserId);

            await AnExpense(group.Id, 90m);

            // Nothing of theirs is in this group at all, so deleting the account is not
            // blocked by it and takes nothing with it.
            var outstanding = await GetService<IAccountService>().DeleteAccount(carlos.Id, Ct);

            Assert.Empty(outstanding);
        }

        Assert.Single(await Invitations.ForGroup(group.Id, Ct));

        await AssertSharesReconcile(group.Id);
        await Balances(group.Id);
    }

    /// <summary>
    /// A group with nobody left to hand a position to keeps it rather than losing it.
    /// </summary>
    /// <remarks>
    /// The narrow case a review caught, and the reason it matters is not how likely it is.
    /// <c>TransactionSplit.UserId</c> and <c>Transaction.UserId</c> are required, so their
    /// foreign keys cascade: deleting a stand-in that still holds shares takes those rows
    /// with it, in silence. The splits left on those expenses would stop summing to the
    /// amount and the group's balances would stop summing to zero -- the one thing this
    /// design exists to prevent, arrived at by tidying up.
    /// <para>
    /// So closing an invitation deletes the stand-in only once its position has actually
    /// moved. Here it cannot move: the group has no members, which an account deleting
    /// itself can produce, since that detaches without the last-member check that leaving
    /// applies.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Closing_an_invitation_with_nobody_to_absorb_it_keeps_the_shares()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 90m);

        // The group empties. DetachMember is what an account deleting itself calls, and it
        // asks nothing about who is left.
        var tracked = await DbContext.Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == group.Id, Ct);

        var me = await DbContext.Set<Data.Entities.User>()
            .FirstAsync(user => user.Id == TestUser().Id, Ct);

        await Groups.DetachMember(tracked, me, Ct);
        await DbContext.SaveChangesAsync(Ct);

        // Declined by whoever holds the link, which needs no membership -- so this is
        // reachable even with the group empty.
        var (scope, _) = await Signing();

        InvitationClosedResponse closed;

        using (scope)
        {
            closed = await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Decline(invitation.Token, Ct);
        }

        // Nothing was handed anywhere, and the answer says so rather than claiming it was.
        Assert.False(closed.MovedAnything);
        Assert.Null(closed.AbsorbedByUserId);

        // The shares are still there, and the expense still divides into exactly its own
        // amount. Untracked: the decline ran in a scope of its own.
        var stored = await DbContext.Set<TransactionSplit>()
            .AsNoTracking()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, stored.Count);
        Assert.Equal(90m, stored.Sum(split => split.Amount));
        Assert.Contains(stored, split => split.UserId == invitation.ParticipantUserId);

        // And the row those shares belong to survives, because deleting it is what would
        // have taken them.
        Assert.NotNull(await DbContext.Set<Data.Entities.User>()
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == invitation.ParticipantUserId, Ct));
    }

    /// <summary>
    /// A rule left with nothing to divide by says so, where it used to be a 500.
    /// </summary>
    /// <remarks>
    /// Withdrawing takes the invitee out of the rules that named them, exactly as a
    /// departure does, and a shares rule that named one person keeps none. SplitCalculator
    /// refuses to divide by nothing, which is right of it -- but an ArgumentException is
    /// nobody's domain error, so it reached the client as a 500, and the person it happened
    /// to was somebody recording a dinner under that category.
    /// </remarks>
    [Fact]
    public async Task An_expense_under_a_rule_that_names_nobody_is_refused_by_name()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        // A rule naming the invitee and nobody else, which is an ordinary thing to write:
        // "Carlos pays for the tickets".
        var category = await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int> { [invitation.ParticipantUserId] = 1 }
        });

        await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        var refused = await Assert.ThrowsAsync<ValidationException>(() =>
            Transactions.Create(new CreateTransactionRequest
            {
                GroupId = group.Id,
                CategoryId = category,
                Amount = 40m,
                DateTime = DateTimeOffset.UtcNow,
                Name = "Tickets"
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.SplitRuleInvalid, refused.Code);

        // Naming the rule is the useful half of the answer: there is nothing wrong with the
        // expense, and the person has to know which template to go and fix.
        Assert.Contains("Tickets", refused.Message);
    }

    /// <summary>
    /// Claiming keeps the place a rule gave them, where it used to take it away.
    /// </summary>
    /// <remarks>
    /// One hand-over served both directions, and they want opposite things. Somebody
    /// leaving loses their place, because a rule that went on naming them would keep giving
    /// them a share of every later expense. Somebody <em>arriving</em> should keep it: the
    /// group wrote "Carlos gets one share" and meant it, and Carlos claiming his link is the
    /// group getting what it asked for.
    /// <para>
    /// Pruning on the way in was silent. The rule simply stopped naming the person who had
    /// just joined, and the next expense under that category divided between everybody else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Claiming_keeps_the_share_a_rule_gave_them()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        var category = await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int>
            {
                [TestUser().Id] = 2,
                [invitation.ParticipantUserId] = 1
            }
        });

        var (scope, carlos) = await Signing();

        InvitationClaimedResponse claimed;

        using (scope)
        {
            claimed = await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Claim(invitation.Token, Ct);
        }

        // Counted in the answer, because keeping the place quietly is how somebody joins a
        // group and finds out weeks later that a category they never chose has been giving
        // them a share of every expense filed under it. The shares and payments are the
        // past; this is the part that goes on happening.
        Assert.Equal(1, claimed.RulesTaken);
        Assert.True(claimed.TookOnARule);

        // The rule names the person who joined, with the weight it gave their stand-in.
        var named = await DbContext.Set<SplitRuleParticipant>()
            .AsNoTracking()
            .Where(participant => participant.SplitRule.Group.Id == group.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, named.Count);
        Assert.Equal(1, named.Single(participant => participant.UserId == carlos.Id).Weight);

        // And it still divides the way the group wrote it: two thirds to me, one to Carlos.
        //
        // Recorded from a scope of its own, which is not ceremony. This test's own context
        // has been tracking those rule participants since it created them, and EF hands back
        // what it is tracking rather than what the store now says -- so the splitter would
        // divide by the weights as they were before the claim. Every request gets a fresh
        // context; a test that reuses one is the only place this can bite.
        using var fresh = AsMember(TestUser().Id);

        var expense = await fresh.ServiceProvider.GetRequiredService<ITransactionService>()
            .Create(new CreateTransactionRequest
            {
                GroupId = group.Id,
                CategoryId = category,
                Amount = 90m,
                DateTime = DateTimeOffset.UtcNow,
                Name = "Tickets"
            }, Ct);

        var stored = await DbContext.Set<TransactionSplit>()
            .AsNoTracking()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(60m, stored.Single(split => split.UserId == TestUser().Id).Amount);
        Assert.Equal(30m, stored.Single(split => split.UserId == carlos.Id).Amount);
    }

    /// <summary>
    /// A weight the claimer already held and one they inherit become one place.
    /// </summary>
    /// <remarks>
    /// A rule may hold only one opinion about a person's weight, and the unique index on
    /// (rule, user) says so -- the same reason two shares of one transaction are added
    /// together rather than left as two rows.
    /// </remarks>
    [Fact]
    public async Task Claiming_adds_to_a_rule_weight_the_claimer_already_held()
    {
        var group = await AGroup();

        var (scope, carlos) = await Signing();

        using (scope)
        {
            await JoinGroup(group.Id, carlos);

            var invitation = await Invite(group.Id, "Carlos");

            await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int>
                {
                    [carlos.Id] = 1,
                    [invitation.ParticipantUserId] = 2
                }
            });

            await scope.ServiceProvider.GetRequiredService<IInvitationService>()
                .Claim(invitation.Token, Ct);
        }

        var named = await DbContext.Set<SplitRuleParticipant>()
            .AsNoTracking()
            .Where(participant => participant.SplitRule.Group.Id == group.Id)
            .ToListAsync(Ct);

        Assert.Single(named);
        Assert.Equal(carlos.Id, named[0].UserId);
        Assert.Equal(3, named[0].Weight);
    }

    /// <summary>
    /// Withdrawing says when it has left a rule naming nobody.
    /// </summary>
    /// <remarks>
    /// The count is the point, and it is a warning rather than a receipt. A rule with
    /// nobody left in it has changed what it means -- a shares rule refuses the next expense
    /// filed under it, and an even one quietly starts dividing between everybody, since
    /// naming nobody is how an even split says that. Neither is a state anybody asked for,
    /// and this is the moment it can still be said to the person who caused it, which
    /// <c>docs/split-rules-and-membership.md</c> had recorded as the standing gap.
    /// </remarks>
    [Fact]
    public async Task Withdrawing_says_when_it_has_left_a_rule_naming_nobody()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        // Named alone, which is an ordinary thing to write: "Carlos pays for the tickets".
        await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int> { [invitation.ParticipantUserId] = 1 }
        });

        var closed = await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Equal(1, closed.RulesAffected);
        Assert.Equal(1, closed.RulesEmptied);
    }

    /// <summary>
    /// A rule that still has somebody in it is not reported as emptied.
    /// </summary>
    [Fact]
    public async Task Withdrawing_says_nothing_about_a_rule_that_still_names_somebody()
    {
        var group = await AGroup();
        var invitation = await Invite(group.Id, "Carlos");

        await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int>
            {
                [TestUser().Id] = 2,
                [invitation.ParticipantUserId] = 1
            }
        });

        var closed = await Invitations.Withdraw(group.Id, invitation.Id, Ct);

        Assert.Equal(1, closed.RulesAffected);
        Assert.Equal(0, closed.RulesEmptied);

        // And what was theirs is divided among the rest rather than left as a hole, which is
        // the doctrine a departure already follows.
        var expense = await AnExpense(group.Id, 90m);

        var stored = await DbContext.Set<TransactionSplit>()
            .AsNoTracking()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(90m, Assert.Single(stored).Amount);
    }

    // ---- Fixtures ---------------------------------------------------------------------

    /// <summary>
    /// A scope acting as somebody who already has an account, for the tests where the act
    /// belongs to a member who is not the one the fixture signed in as.
    /// </summary>
    private IServiceScope AsMember(Guid userId)
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();

        // Re-read inside the scope. Handing it an entity another context is tracking makes
        // the second one refuse the moment it meets the same row again -- "another instance
        // with the same key value is already being tracked" -- which is the whole reason
        // these scopes exist: a request gets its own context and its own instances.
        var user = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>()
            .Set<Data.Entities.User>()
            .First(candidate => candidate.Id == userId);

        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().Initialize(user);

        return scope;
    }

    /// <summary>The account the test itself is signed in as.</summary>
    private Data.Entities.User TestUser() => GetService<ICurrentUser>().User;

    /// <summary>
    /// Somebody with an account of their own, in a scope of their own -- the person who will
    /// open a link.
    /// </summary>
    /// <remarks>
    /// Nothing connects them to the invitation in advance, which is the point: a link names
    /// nobody in particular, and claiming is what makes the position theirs.
    /// </remarks>
    private async Task<(IServiceScope Scope, Data.Entities.User User)> Signing()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();

        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }
}

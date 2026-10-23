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
/// A guest -- somebody the group shares costs with who has no account in it -- is a
/// participant in the group's money: they can be given a share, be the payer, be settled
/// with, and be removed once square.
/// </summary>
/// <remarks>
/// The invariant every test here is really about is the one the whole ledger rests on: each
/// transaction's shares sum to its amount, and the group's balances sum to zero. Adding a
/// kind of participant is the sort of change that breaks it quietly, and nothing else
/// catches it -- so most of these check the arithmetic as well as the behaviour.
/// </remarks>
public class GuestTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IGuestService Guests => GetService<IGuestService>();
    private IInvitationService Invitations => GetService<IInvitationService>();
    private IGroupService Groups => GetService<IGroupService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private ISplitRuleService Rules => GetService<ISplitRuleService>();
    private ISettlementService Settlements => GetService<ISettlementService>();
    private IGroupParticipants Participants => GetService<IGroupParticipants>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "The flat") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    /// <summary>Adds one guest, with no link, and hands back the row.</summary>
    private async Task<GroupGuestResponse> AddGuest(Guid groupId, string name) =>
        Assert.Single(await Guests.Add(groupId, new AddGuestsRequest { Names = [name] }, Ct));

    /// <summary>The token of a link issued for the guest, for whoever will claim it.</summary>
    private async Task<string> LinkFor(GroupGuestResponse guest) =>
        (await Guests.IssueLink(guest.GroupId, guest.UserId, replace: false, Ct)).Token!;

    private Task<Expense> AnExpense(Guid groupId, decimal amount,
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
    /// Untracked: a claim happens in a scope of its own, so this context may already be
    /// holding the split rows as they were before it.
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
            Assert.Equal(transaction.Amount, transaction.Splits.Sum(split => split.Amount));
    }

    // ---- Adding ------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_a_guest_invitation_notifies_opted_in_existing_members()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);
        var owner = GetService<ICurrentUser>().User;
        DbContext.Add(new GroupSplit.Data.Entities.PushDevice { UserId = owner.Id, Token = "test-token",
            UpdatedAt = DateTimeOffset.UtcNow, InvitationsEnabled = true });
        await DbContext.SaveChangesAsync(Ct);
        var (scope, carlos) = await Signing();
        using (scope)
            await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);
        var jobs = DbContext.Set<GroupSplit.Data.Entities.BackgroundJob>()
            .Where(j => j.Type == GroupSplit.API.Services.Push.SendPushNotification.Name).ToList();
        var stored = Assert.Single(jobs);
        var job = System.Text.Json.JsonSerializer.Deserialize<GroupSplit.API.Services.Push.SendPushNotification>(stored.Payload)!;
        Assert.Equal(owner.Id, job.UserId);
        Assert.Equal(group.Id, job.GroupId);
        Assert.Equal(PushNotificationKind.InvitationAccepted, job.Kind);
    }

    [Fact]
    public async Task Adding_a_guest_makes_a_stand_in_and_their_rule_and_no_link()
    {
        var group = await AGroup();

        var guest = await AddGuest(group.Id, "Carlos");

        Assert.Equal("Carlos", guest.Name);
        Assert.False(guest.HasLink);
        Assert.Null(guest.Token);

        var standIn = await DbContext.Set<Data.Entities.User>().AsNoTracking().FirstAsync(user => user.Id == guest.UserId, Ct);
        Assert.Equal(UserKind.StandIn, standIn.Kind);
        Assert.Null(standIn.Email);

        Assert.True(await DbContext.Set<SplitRule>()
            .AnyAsync(rule => rule.BuiltIn && rule.Name == "All for Carlos" &&
                              rule.Versions.OfType<SoleSplitRuleVersion>()
                                  .Any(version => version.SupersededAt == null && version.UserId == guest.UserId), Ct));

        // And the old route, which lists guests with a link, has nothing to show.
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
    }

    [Fact]
    public async Task Adding_a_guest_is_written_into_the_membership_history()
    {
        var group = await AGroup();

        var guest = await AddGuest(group.Id, "Carlos");

        var added = Assert.Single(await DbContext.Set<GroupMembershipEvent>()
            .AsNoTracking()
            .Where(change => change.GroupId == group.Id && change.UserId == guest.UserId)
            .ToListAsync(Ct));

        Assert.Equal(MembershipEventKind.GuestAdded, added.Kind);
        Assert.Equal(TestUser().Id, added.ByUserId);
    }

    [Fact]
    public async Task Adding_with_links_issues_one_each()
    {
        var group = await AGroup();

        var added = await Guests.Add(group.Id,
            new AddGuestsRequest { Names = ["Carlos", "Dani"], IssueLinks = true }, Ct);

        Assert.Equal(["Carlos", "Dani"], added.Select(guest => guest.Name));
        Assert.All(added, guest => Assert.True(guest.HasLink));
        Assert.Equal(2, added.Select(guest => guest.Token).Distinct().Count());
    }

    [Fact]
    public async Task Adding_nobody_is_refused()
    {
        var group = await AGroup();

        var refused = await Assert.ThrowsAsync<ValidationException>(() =>
            Guests.Add(group.Id, new AddGuestsRequest { Names = ["  "] }, Ct));

        Assert.Equal(ErrorCodes.GroupInvitationNoName, refused.Code);
    }

    // ---- Being choosable ---------------------------------------------------------------

    [Fact]
    public async Task A_guest_is_somebody_the_group_can_give_a_share_to()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 90m, splits:
        [
            new SplitInput { UserId = TestUser().Id, Amount = 60m },
            new SplitInput { UserId = guest.UserId, Amount = 30m }
        ]);

        var stored = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, stored.Count);
        Assert.Equal(30m, stored.Single(split => split.UserId == guest.UserId).Amount);
    }

    /// <summary>
    /// Nothing has to name them for them to be included: an even division is between the
    /// group's participants, and they are one.
    /// </summary>
    [Fact]
    public async Task An_even_split_divides_between_guests_as_well_as_members()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 90m);

        var stored = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, stored.Count);
        Assert.Equal(45m, stored.Single(split => split.UserId == guest.UserId).Amount);
    }

    [Fact]
    public async Task A_guest_can_be_the_payer_of_an_expense()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var expense = await AnExpense(group.Id, 80m, payer: guest.UserId);

        Assert.Equal(guest.UserId, expense.UserId);

        // They fronted it and owe half of it, so the group owes them the other half.
        Assert.Equal(40m, (await Balances(group.Id)).Single(b => b.UserId == guest.UserId).Balance);
    }

    [Fact]
    public async Task A_split_rule_can_name_a_guest()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Two thirds mine",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [TestUser().Id] = 2, [guest.UserId] = 1 }
            }
        }, Ct);

        Assert.True(await DbContext.Set<SplitRuleParticipant>()
            .AnyAsync(participant => participant.SplitRuleVersion.SplitRuleId == rule.Id &&
                                     participant.UserId == guest.UserId, Ct));
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
                Definition = new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [stranger.Id] = 1 } }
            }, Ct));

        Assert.Equal(ErrorCodes.RuleUsersNotInGroup, refused.Code);
    }

    // ---- Being visible ----------------------------------------------------------------

    [Fact]
    public async Task The_members_listing_says_which_of_them_are_guests()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var people = await (await Groups.GetGroupMembers(group.Id, Ct)).ToListAsync(Ct);

        Assert.Equal(2, people.Count);

        var carlos = people.Single(person => person.Id == guest.UserId);

        Assert.True(await Participants.IsGuest(group.Id, carlos.Id, Ct));
        Assert.False(await Participants.IsGuest(group.Id, TestUser().Id, Ct));
        Assert.Equal(ParticipantStanding.Guest, await Participants.StandingOf(group.Id, carlos.Id, Ct));

        Assert.Equal("Carlos", carlos.FirstName);
        Assert.Null(carlos.Email);
    }

    [Fact]
    public async Task Their_balance_is_in_the_group_and_marked_as_a_guest()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var balances = await Balances(group.Id);
        var theirs = balances.Single(balance => balance.UserId == guest.UserId);

        Assert.True(theirs.IsGuest);
        Assert.Equal(-45m, theirs.Balance);
        Assert.Equal("Carlos", theirs.UserName);
        Assert.False(balances.Single(balance => balance.UserId == TestUser().Id).IsGuest);
    }

    // ---- Settling with them -----------------------------------------------------------

    /// <summary>
    /// The plan clears everybody, guests included: the members record what a guest pays and
    /// is paid, so a line naming one is something somebody can do.
    /// </summary>
    [Fact]
    public async Task The_settlement_plan_includes_them()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var position = await GetService<IDebtCalculationService>().GetUserBalance(await Balances(group.Id));

        var line = Assert.Single(position.Plan);
        Assert.Equal(guest.UserId, line.FromUserId);
        Assert.Equal(TestUser().Id, line.ToUserId);
        Assert.Equal(45m, line.Amount);

        Assert.Equal(guest.UserId, Assert.Single(position.OwedToYou).UserId);
    }

    /// <summary>
    /// The cross-group plan names them too, and says they are a guest -- so "owed to you"
    /// counts the same people the balances do.
    /// </summary>
    [Fact]
    public async Task The_cross_group_plan_names_them_as_a_guest()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var plan = await Settlements.GetPlan(Ct);

        var carlos = Assert.Single(plan.OwedToYou);
        Assert.Equal(guest.UserId, carlos.UserId);
        Assert.True(carlos.IsGuest);
        Assert.Equal(45m, carlos.Amount);
        Assert.Equal(45m, plan.OwedToYouTotal);
    }

    [Fact]
    public async Task A_member_records_that_a_guest_paid_them_back()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        await Settlements.Settle(group.Id,
            new SettleRequest { UserId = guest.UserId, Amount = 45m, Direction = SettlementDirection.TheyPaidYou }, Ct);

        Assert.All(await Balances(group.Id), balance => Assert.Equal(0m, balance.Balance));
    }

    [Fact]
    public async Task A_member_records_paying_a_guest()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 90m, payer: guest.UserId);

        await Settlements.Settle(group.Id,
            new SettleRequest { UserId = guest.UserId, Amount = 45m, Direction = SettlementDirection.YouPaidThem }, Ct);

        Assert.All(await Balances(group.Id), balance => Assert.Equal(0m, balance.Balance));
    }

    /// <summary>
    /// Either end, or both: one guest paying another back is something the members can see
    /// happen and write down.
    /// </summary>
    [Fact]
    public async Task A_repayment_can_be_recorded_between_two_guests()
    {
        var group = await AGroup();
        var carlos = await AddGuest(group.Id, "Carlos");
        var dani = await AddGuest(group.Id, "Dani");

        await AnExpense(group.Id, 60m, payer: carlos.UserId, splits:
        [
            new SplitInput { UserId = carlos.UserId, Amount = 30m },
            new SplitInput { UserId = dani.UserId, Amount = 30m }
        ]);

        var recorded = await Settlements.RecordRepayment(group.Id, new RecordRepaymentRequest
        {
            FromUserId = dani.UserId,
            ToUserId = carlos.UserId,
            Amount = 30m
        }, Ct);

        Assert.Equal("Dani", recorded.FromUserName);
        Assert.Equal("Carlos", recorded.ToUserName);
        Assert.All(await Balances(group.Id), balance => Assert.Equal(0m, balance.Balance));
    }

    [Fact]
    public async Task A_repayment_naming_somebody_outside_the_group_is_still_refused()
    {
        var group = await AGroup();
        var stranger = await CreateNewUser();

        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements.RecordRepayment(group.Id, new RecordRepaymentRequest
            {
                FromUserId = stranger.Id,
                ToUserId = TestUser().Id,
                Amount = 10m
            }, Ct));

        Assert.Equal(ErrorCodes.UserNotFound, refused.Code);
    }

    /// <summary>
    /// A member owed only by a guest could not leave before: nobody could record the guest
    /// paying them back. Now they can, and then they can go.
    /// </summary>
    [Fact]
    public async Task A_member_owed_only_by_a_guest_can_be_paid_back_and_leave()
    {
        var group = await AGroup();
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 40m, splits: [new SplitInput { UserId = guest.UserId, Amount = 40m }]);

        await Settlements.RecordRepayment(group.Id, new RecordRepaymentRequest
        {
            FromUserId = guest.UserId,
            ToUserId = TestUser().Id,
            Amount = 40m
        }, Ct);

        await Groups.Leave(group.Id, Ct);

        Assert.False(await DbContext.Set<GroupMembership>()
            .AnyAsync(membership => membership.GroupId == group.Id && membership.UserId == TestUser().Id, Ct));
    }

    // ---- Removing them ----------------------------------------------------------------

    [Fact]
    public async Task Removing_a_guest_who_is_not_square_is_refused_with_their_balance()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await AnExpense(group.Id, 90m);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Groups.RemoveGroupMember(group.Id, guest.UserId, Ct));

        Assert.Equal(ErrorCodes.GroupMemberNotSettled, refused.Code);
        Assert.Equal(-45m, refused.Extensions["balance"]);
        Assert.True(await Participants.IsGuest(group.Id, guest.UserId, Ct));
    }

    /// <summary>
    /// Once square they go -- and stay in the history, like a member who left: out of the
    /// people the group can name, among its past participants, and their shares fixed.
    /// </summary>
    [Fact]
    public async Task Removing_a_square_guest_keeps_them_as_somebody_who_has_gone()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        var expense = await AnExpense(group.Id, 90m);

        await Settlements.Settle(group.Id,
            new SettleRequest { UserId = guest.UserId, Amount = 45m, Direction = SettlementDirection.TheyPaidYou }, Ct);

        await Groups.RemoveGroupMember(group.Id, guest.UserId, Ct);

        Assert.Equal(ParticipantStanding.Departed, await Participants.StandingOf(group.Id, guest.UserId, Ct));

        var people = await (await Groups.GetGroupMembers(group.Id, Ct)).Select(user => user.Id).ToListAsync(Ct);
        Assert.DoesNotContain(guest.UserId, people);

        var past = await (await Groups.GetGroupPastMembers(group.Id, Ct)).Select(user => user.Id).ToListAsync(Ct);
        Assert.Contains(guest.UserId, past);

        Assert.Empty(await Guests.ForGroup(group.Id, Ct));

        // Their link stopped working with them.
        var gone = await Assert.ThrowsAsync<NotFoundException>(() => Invitations.Describe(token, Ct));
        Assert.Equal(ErrorCodes.GroupInvitationNotFound, gone.Code);

        // The row ended rather than going, and the history says so.
        var row = await DbContext.Set<GroupGuest>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(candidate => candidate.ParticipantUserId == guest.UserId, Ct);
        Assert.Equal(GuestEndReason.Removed, row.EndReason);
        Assert.Equal(TestUser().Id, row.EndedByUserId);
        Assert.Null(row.Token);

        Assert.Contains(await DbContext.Set<GroupMembershipEvent>().AsNoTracking()
                .Where(change => change.UserId == guest.UserId)
                .Select(change => change.Kind)
                .ToListAsync(Ct),
            kind => kind == MembershipEventKind.GuestRemoved);

        // And what they held on the expense cannot move now that they have gone.
        var refused = await Assert.ThrowsAsync<ConflictException>(() => Transactions.Delete(expense.Id, Ct));
        Assert.Equal(ErrorCodes.TransactionMovesDepartedBalance, refused.Code);
    }

    [Fact]
    public async Task A_removed_guest_cannot_be_named_for_new_money()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await Groups.RemoveGroupMember(group.Id, guest.UserId, Ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            AnExpense(group.Id, 10m, payer: guest.UserId));

        Assert.Equal(ErrorCodes.TransactionPayerNotInGroup, refused.Code);

        var notHere = await Assert.ThrowsAsync<NotFoundException>(() => Settlements.Settle(group.Id,
            new SettleRequest { UserId = guest.UserId, Amount = 1m }, Ct));
        Assert.Equal(ErrorCodes.UserNotFound, notHere.Code);
    }

    [Fact]
    public async Task Removing_a_guest_twice_is_a_404()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        await Groups.RemoveGroupMember(group.Id, guest.UserId, Ct);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            Groups.RemoveGroupMember(group.Id, guest.UserId, Ct));

        Assert.Equal(ErrorCodes.UserNotFound, refused.Code);
    }

    /// <summary>
    /// Removing a member revokes the group's join link, because they could have passed it
    /// on. A guest never had a way to read it, so removing one leaves it alone.
    /// </summary>
    [Fact]
    public async Task Removing_a_guest_leaves_the_join_link_standing()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var link = await GetService<IJoinLinkService>().Create(group.Id, Ct);

        await Groups.RemoveGroupMember(group.Id, guest.UserId, Ct);

        var standing = Assert.Single(await GetService<IJoinLinkService>().ForGroup(group.Id, Ct));
        Assert.Equal(link.Token, standing.Token);
    }

    [Fact]
    public async Task Removing_a_guest_takes_them_out_of_the_rules_that_named_them()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Even between us",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [TestUser().Id] = 1, [guest.UserId] = 1 }
            }
        }, Ct);

        await Groups.RemoveGroupMember(group.Id, guest.UserId, Ct);

        var named = await DbContext.Set<SplitRuleParticipant>()
            .AsNoTracking()
            .Where(participant => participant.SplitRuleVersion.SplitRuleId == rule.Id &&
                                  participant.SplitRuleVersion.SupersededAt == null)
            .ToListAsync(Ct);

        Assert.Equal(TestUser().Id, Assert.Single(named).UserId);
    }

    /// <summary>
    /// A rule left with nothing to divide by says so, by name, rather than failing.
    /// </summary>
    [Fact]
    public async Task An_expense_under_a_rule_that_names_nobody_is_refused_by_name()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var category = await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int> { [guest.UserId] = 1 }
        });

        await Groups.RemoveGroupMember(group.Id, guest.UserId, Ct);

        using var fresh = AsMember(TestUser().Id);

        var refused = await Assert.ThrowsAsync<ValidationException>(() =>
            fresh.ServiceProvider.GetRequiredService<ITransactionService>().Create(new CreateTransactionRequest
            {
                GroupId = group.Id,
                CategoryId = category,
                Amount = 40m,
                DateTime = DateTimeOffset.UtcNow,
                Name = "Tickets"
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.SplitRuleInvalid, refused.Code);
        Assert.Contains("Tickets", refused.Message);
    }

    // ---- Their link -------------------------------------------------------------------

    [Fact]
    public async Task Asking_for_a_link_twice_gives_the_same_one()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var first = await Guests.IssueLink(group.Id, guest.UserId, replace: false, Ct);
        var second = await Guests.IssueLink(group.Id, guest.UserId, replace: false, Ct);

        Assert.True(first.HasLink);
        Assert.Equal(first.Token, second.Token);
        Assert.NotNull(first.LinkIssuedAt);
    }

    /// <summary>
    /// A replaced link is dead, and somebody who opened it is not handed the new one --
    /// which is the whole reason to replace a link that leaked.
    /// </summary>
    [Fact]
    public async Task Replacing_a_link_kills_the_old_one_and_forgets_who_opened_it()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var old = await LinkFor(guest);

        var guestRow = await DbContext.Set<GroupGuest>().AsNoTracking()
            .Where(row => row.ParticipantUserId == guest.UserId)
            .Select(row => row.Id)
            .SingleAsync(Ct);

        var (scope, opener) = await Signing();

        using (scope)
        {
            var theirs = scope.ServiceProvider.GetRequiredService<IInvitationService>();

            await theirs.Describe(old, Ct);
            Assert.Single(await theirs.Mine(Ct));
        }

        // Written down for this guest, by this opener -- so its absence below means
        // something.
        Assert.True(await DbContext.Set<InvitationOpened>()
            .AnyAsync(opened => opened.GuestId == guestRow && opened.UserId == opener.Id, Ct));

        var replaced = await Guests.IssueLink(group.Id, guest.UserId, replace: true, Ct);

        Assert.NotEqual(old, replaced.Token);

        await Assert.ThrowsAsync<NotFoundException>(() => Invitations.Describe(old, Ct));

        Assert.False(await DbContext.Set<InvitationOpened>().AsNoTracking()
            .AnyAsync(opened => opened.GuestId == guestRow, Ct));

        using var again = AsMember(opener.Id);
        Assert.Empty(await again.ServiceProvider.GetRequiredService<IInvitationService>().Mine(Ct));
    }

    [Fact]
    public async Task Stopping_a_link_keeps_the_guest()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        await AnExpense(group.Id, 90m);

        await Guests.RevokeLink(group.Id, guest.UserId, Ct);

        await Assert.ThrowsAsync<NotFoundException>(() => Invitations.Describe(token, Ct));

        var still = Assert.Single(await Guests.ForGroup(group.Id, Ct));
        Assert.False(still.HasLink);
        Assert.Equal(-45m, (await Balances(group.Id)).Single(b => b.UserId == guest.UserId).Balance);
    }

    [Fact]
    public async Task A_link_for_somebody_who_is_not_a_guest_is_a_404()
    {
        var group = await AGroup();

        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            Guests.IssueLink(group.Id, TestUser().Id, replace: false, Ct));

        Assert.Equal(ErrorCodes.GroupGuestNotFound, refused.Code);
    }

    /// <summary>
    /// "This isn't me" is a fact about the link, not the guest: the person the group spent
    /// with is still the person it spent with.
    /// </summary>
    [Fact]
    public async Task Declining_stops_the_link_and_moves_nothing()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        await AnExpense(group.Id, 90m);

        var (scope, _) = await Signing();

        InvitationClosedResponse closed;

        using (scope)
        {
            closed = await scope.ServiceProvider.GetRequiredService<IInvitationService>().Decline(token, Ct);
        }

        Assert.Equal(InvitationOutcome.Declined, closed.Outcome);
        Assert.False(closed.MovedAnything);
        Assert.Null(closed.AbsorbedByUserId);

        Assert.True(await Participants.IsGuest(group.Id, guest.UserId, Ct));
        Assert.Equal(-45m, (await Balances(group.Id)).Single(b => b.UserId == guest.UserId).Balance);

        await Assert.ThrowsAsync<NotFoundException>(() => Invitations.Describe(token, Ct));

        // And a member can send a new one.
        Assert.NotEqual(token, await LinkFor(guest));
    }

    [Fact]
    public async Task Withdrawing_by_the_old_route_stops_the_link_and_keeps_the_guest()
    {
        var group = await AGroup();
        var invited = Assert.Single(await Invitations.Invite(group.Id, new InviteToGroupRequest { Names = ["Carlos"] }, Ct));

        await AnExpense(group.Id, 90m);

        var closed = await Invitations.Withdraw(group.Id, invited.Id, Ct);

        Assert.Equal(InvitationOutcome.Withdrawn, closed.Outcome);
        Assert.False(closed.MovedAnything);
        Assert.Empty(await Invitations.ForGroup(group.Id, Ct));
        Assert.True(await Participants.IsGuest(group.Id, invited.ParticipantUserId, Ct));
    }

    // ---- Claiming -----------------------------------------------------------------------

    /// <summary>
    /// Claiming changes no amount and asks nothing of anybody: the position simply stops
    /// being a stand-in's and starts being an account's.
    /// </summary>
    [Fact]
    public async Task Claiming_makes_the_history_theirs_without_changing_anything()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        await AnExpense(group.Id, 90m);
        await AnExpense(group.Id, 40m, payer: guest.UserId);

        var theirs = (await Balances(group.Id)).Single(balance => balance.UserId == guest.UserId);

        var (scope, carlos) = await Signing();

        InvitationClaimedResponse claimed;

        using (scope)
        {
            claimed = await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);
        }

        Assert.Equal(2, claimed.SharesTaken);
        Assert.Equal(65m, claimed.AmountOwed);
        Assert.Equal(1, claimed.PaymentsTaken);
        Assert.Equal(40m, claimed.AmountPaid);
        Assert.Equal(0, claimed.SettlementsCancelled);

        var mine = (await Balances(group.Id)).Single(balance => balance.UserId == carlos.Id);

        Assert.Equal(theirs.Balance, mine.Balance);
        Assert.Equal(theirs.AmountOwed, mine.AmountOwed);
        Assert.Equal(theirs.AmountPaid, mine.AmountPaid);
        Assert.False(mine.IsGuest);

        Assert.Empty(await Guests.ForGroup(group.Id, Ct));

        await AssertSharesReconcile(group.Id);
    }

    /// <summary>
    /// The guest ends rather than going, and the stand-in stays: it is what the history
    /// names for the time before the claim, and it holds nothing now.
    /// </summary>
    [Fact]
    public async Task Claiming_ends_the_guest_and_keeps_the_stand_in_as_nobody()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        await AnExpense(group.Id, 90m);

        var (scope, carlos) = await Signing();

        using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);
        }

        var row = await DbContext.Set<GroupGuest>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(candidate => candidate.ParticipantUserId == guest.UserId, Ct);

        Assert.Equal(GuestEndReason.Claimed, row.EndReason);
        Assert.Equal(carlos.Id, row.EndedByUserId);
        Assert.Null(row.Token);

        Assert.True(await DbContext.Set<Data.Entities.User>().AnyAsync(user => user.Id == guest.UserId, Ct));
        Assert.Equal(ParticipantStanding.Stranger, await Participants.StandingOf(group.Id, guest.UserId, Ct));

        var past = await (await Groups.GetGroupPastMembers(group.Id, Ct)).Select(user => user.Id).ToListAsync(Ct);
        Assert.DoesNotContain(guest.UserId, past);

        var history = await DbContext.Set<GroupMembershipEvent>().AsNoTracking()
            .Where(change => change.GroupId == group.Id)
            .Select(change => new { change.UserId, change.Kind })
            .ToListAsync(Ct);

        Assert.Contains(history, change => change.UserId == guest.UserId && change.Kind == MembershipEventKind.GuestClaimed);
        Assert.Contains(history, change => change.UserId == carlos.Id && change.Kind == MembershipEventKind.Joined);

        var again = await Assert.ThrowsAsync<NotFoundException>(() => Invitations.Describe(token, Ct));
        Assert.Equal(ErrorCodes.GroupInvitationNotFound, again.Code);
    }

    /// <summary>
    /// A claimed guest whose stand-in an old rule version still names is nobody, not somebody
    /// who left -- in the standing and in the past-members listing alike.
    /// </summary>
    [Fact]
    public async Task A_claimed_guest_still_named_by_rule_history_is_a_stranger()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Shares",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [TestUser().Id] = 1, [guest.UserId] = 1 }
            }
        }, Ct);

        // Edited once, so a superseded version names the stand-in for good.
        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Shares",
            Definition = new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [TestUser().Id] = 2, [guest.UserId] = 1 }
            }
        }, Ct);

        var (scope, _) = await Signing();

        using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);
        }

        Assert.Equal(ParticipantStanding.Stranger, await Participants.StandingOf(group.Id, guest.UserId, Ct));

        var past = await (await Groups.GetGroupPastMembers(group.Id, Ct)).Select(user => user.Id).ToListAsync(Ct);
        Assert.DoesNotContain(guest.UserId, past);
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

            var guest = await AddGuest(group.Id, "Carlos");
            var token = await LinkFor(guest);

            var expense = await AnExpense(group.Id, 90m, splits:
            [
                new SplitInput { UserId = TestUser().Id, Amount = 30m },
                new SplitInput { UserId = carlos.Id, Amount = 30m },
                new SplitInput { UserId = guest.UserId, Amount = 30m }
            ]);

            await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);

            var stored = await DbContext.Set<TransactionSplit>()
                .AsNoTracking()
                .Where(split => split.TransactionId == expense.Id)
                .ToListAsync(Ct);

            Assert.Equal(2, stored.Count);
            Assert.Equal(60m, stored.Single(split => split.UserId == carlos.Id).Amount);
        }

        await AssertSharesReconcile(group.Id);
        await Balances(group.Id);
    }

    /// <summary>
    /// A member claiming a guest who turns out to be them: repayments recorded between the
    /// two would become the member paying themselves, so they go -- and the member's balance
    /// is exactly what the two of them came to together.
    /// </summary>
    [Fact]
    public async Task Claiming_cancels_repayments_between_the_guest_and_the_claimer()
    {
        var group = await AGroup();

        var (scope, carlos) = await Signing();

        using (scope)
        {
            await JoinGroup(group.Id, carlos);

            var guest = await AddGuest(group.Id, "Carlos again");
            var token = await LinkFor(guest);

            await AnExpense(group.Id, 90m);

            // Recorded both ways before anybody noticed they were the same person.
            await Settlements.RecordRepayment(group.Id, new RecordRepaymentRequest
            {
                FromUserId = guest.UserId, ToUserId = carlos.Id, Amount = 10m
            }, Ct);
            await Settlements.RecordRepayment(group.Id, new RecordRepaymentRequest
            {
                FromUserId = carlos.Id, ToUserId = guest.UserId, Amount = 4m
            }, Ct);

            var before = await Balances(group.Id);
            var together = before.Where(b => b.UserId == carlos.Id || b.UserId == guest.UserId).Sum(b => b.Balance);

            var claimed = await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);

            Assert.Equal(2, claimed.SettlementsCancelled);
            Assert.Equal(14m, claimed.AmountCancelled);

            DbContext.ChangeTracker.Clear();

            var after = await Balances(group.Id);
            Assert.Equal(together, after.Single(b => b.UserId == carlos.Id).Balance);

            Assert.False(await DbContext.Set<Transfer>()
                .AnyAsync(transfer => transfer.GroupId == group.Id &&
                                      transfer.Splits.Any(split => split.UserId == transfer.UserId), Ct));
        }

        await AssertSharesReconcile(group.Id);
    }

    /// <summary>
    /// Following a group's open join link is not claiming a guest, and does not end one:
    /// nothing has told the group the two are the same person.
    /// </summary>
    [Fact]
    public async Task Joining_by_the_group_link_leaves_the_guest_standing()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");

        var (scope, carlos) = await Signing();

        using (scope)
        {
            var joiner = scope.ServiceProvider.GetRequiredService<IGroupJoiner>();
            var context = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();

            var tracked = await context.Set<Data.Entities.Group>()
                .Include(candidate => candidate.Users)
                .FirstAsync(candidate => candidate.Id == group.Id, Ct);

            Assert.Equal(JoinOutcome.Joined, await joiner.Join(tracked, carlos, DateTimeOffset.UtcNow, Ct));
        }

        Assert.True(await Participants.IsGuest(group.Id, guest.UserId, Ct));
        Assert.Equal(3, await (await Groups.GetGroupMembers(group.Id, Ct)).CountAsync(Ct));
    }

    /// <summary>
    /// Deleting an account cannot strand a group's money in a guest, because a guest's
    /// stand-in is never an account.
    /// </summary>
    [Fact]
    public async Task A_guest_never_holds_a_real_accounts_money()
    {
        var group = await AGroup();

        var (scope, carlos) = await Signing();

        using (scope)
        {
            var guest = await AddGuest(group.Id, "Carlos");

            Assert.NotEqual(carlos.Id, guest.UserId);

            await AnExpense(group.Id, 90m);

            Assert.Empty(await GetService<IAccountService>().DeleteAccount(carlos.Id, Ct));
        }

        Assert.Single(await Guests.ForGroup(group.Id, Ct));

        await AssertSharesReconcile(group.Id);
        await Balances(group.Id);
    }

    /// <summary>Claiming keeps the place a rule gave them.</summary>
    [Fact]
    public async Task Claiming_keeps_the_share_a_rule_gave_them()
    {
        var group = await AGroup();
        var guest = await AddGuest(group.Id, "Carlos");
        var token = await LinkFor(guest);

        var category = await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
        {
            Shares = new Dictionary<Guid, int> { [TestUser().Id] = 2, [guest.UserId] = 1 }
        });

        var (scope, carlos) = await Signing();

        InvitationClaimedResponse claimed;

        using (scope)
        {
            claimed = await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);
        }

        Assert.Equal(1, claimed.RulesTaken);
        Assert.True(claimed.TookOnARule);

        // Recorded from a scope of its own: this test's context has been tracking the rule
        // participants since it created them, and would divide by the weights as they were.
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

    /// <summary>A weight the claimer already held and one they inherit become one place.</summary>
    [Fact]
    public async Task Claiming_adds_to_a_rule_weight_the_claimer_already_held()
    {
        var group = await AGroup();

        var (scope, carlos) = await Signing();

        using (scope)
        {
            await JoinGroup(group.Id, carlos);

            var guest = await AddGuest(group.Id, "Carlos");
            var token = await LinkFor(guest);

            await CreateCategory(group.Id, "Tickets", new SharesSplitRuleDto
            {
                Shares = new Dictionary<Guid, int> { [carlos.Id] = 1, [guest.UserId] = 2 }
            });

            await scope.ServiceProvider.GetRequiredService<IInvitationService>().Claim(token, Ct);
        }

        var named = await DbContext.Set<SplitRuleParticipant>()
            .AsNoTracking()
            .Where(participant => participant.SplitRuleVersion.SplitRule.Group.Id == group.Id &&
                                  participant.SplitRuleVersion.SupersededAt == null)
            .ToListAsync(Ct);

        var only = Assert.Single(named);
        Assert.Equal(carlos.Id, only.UserId);
        Assert.Equal(3, only.Weight);
    }

    // ---- Fixtures ---------------------------------------------------------------------

    /// <summary>
    /// A scope acting as somebody who already has an account, re-read inside it so the
    /// scope's context has its own instance.
    /// </summary>
    private IServiceScope AsMember(Guid userId)
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();

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
    private async Task<(IServiceScope Scope, Data.Entities.User User)> Signing()
    {
        var scope = GetService<IServiceScopeFactory>().CreateScope();

        await InitializeCurrentUser(scope.ServiceProvider);

        return (scope, scope.ServiceProvider.GetRequiredService<ICurrentUser>().User);
    }
}

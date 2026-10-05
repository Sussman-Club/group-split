using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Dividing an old expense again pays the people who are in the group now, and not the
/// people its version was written naming.
/// </summary>
/// <remarks>
/// The two facts that collide here are both deliberate. A superseded version goes on naming
/// whoever it named, because that is the only record of how an expense from last March was
/// divided; and a departure takes somebody out of the group's balances entirely, because
/// they have settled and gone. Dividing by the first while the second is true handed a
/// share to a person the balance listing no longer contains -- and a group's balances add
/// up only because every share belongs to somebody on the page.
/// <para>
/// An ordinary user reaches it in one click: step two of the edit dialog, "By hand" to
/// "Automatically", or <c>transactions update --redivide</c>. The save stores it.
/// </para>
/// </remarks>
public class DepartedParticipantTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupService Groups => GetService<IGroupService>();

    private IInvitationService Invitations => GetService<IInvitationService>();

    private ITransactionService Transactions => GetService<ITransactionService>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    private static SharesSplitRuleDto Shares(params (Guid UserId, int Weight)[] weights) =>
        new() { Shares = weights.ToDictionary(pair => pair.UserId, pair => pair.Weight) };

    private async Task<(Guid GroupId, Guid Other)> GroupOfTwo()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, other.Id);
    }

    private Task<Expense> AnExpense(Guid groupId, Guid categoryId, decimal amount) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Rent",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId
        }, Ct).AsTask();

    /// <summary>
    /// Asks for the division to be worked out again, which is what the dialog's
    /// "Automatically" and <c>--redivide</c> both come down to: a model with no shares
    /// stated.
    /// </summary>
    private ValueTask<Data.Entities.Transaction> Redivide(Guid expenseId, Guid groupId, Guid categoryId, decimal amount) =>
        Transactions.Update(expenseId, new UpdateTransactionRequest
        {
            Name = "Rent",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = Self,
            GroupId = groupId,
            CategoryId = categoryId
        }, Ct);

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .AsNoTracking()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(Ct);

    /// <summary>
    /// Takes a member out the way an account deletion does, without the endpoint's
    /// settled-up check standing in the way of a test that is about the division.
    /// </summary>
    private async Task Departs(Guid groupId, Guid userId)
    {
        var group = await DbContext.Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == groupId, Ct);

        var user = await DbContext.Set<Data.Entities.User>()
            .FirstAsync(candidate => candidate.Id == userId, Ct);

        await Groups.DetachMember(group, user, Data.Entities.LeftReason.AccountDeleted, user.Id, Ct);
        await DbContext.SaveChangesAsync(Ct);
    }

    private async Task<IReadOnlyList<GroupNetBalance>> Balances(Guid groupId) =>
        await (await Groups.GetGroupNetBalance(groupId, Ct)).ToListAsync(Ct);

    /// <summary>
    /// The group's balances add up. A column summing to anything but zero means money
    /// belonging to nobody on the page.
    /// </summary>
    private async Task AssertBalancesSumToZero(Guid groupId) =>
        Assert.Equal(0m, (await Balances(groupId)).Sum(balance => balance.Balance));

    /// <summary>
    /// The reproduction: a group of two on a one-to-one rule, the second member settles and
    /// goes, and the old expense is divided again.
    /// </summary>
    /// <remarks>
    /// This used to succeed, handing the leaver's 100 to the person still here and leaving
    /// the leaver owed 100 that nobody on the page owed back. It is refused now, by name, and
    /// nothing moves.
    /// </remarks>
    [Fact]
    public async Task A_new_amount_on_an_expense_a_leaver_paid_is_refused_by_their_name()
    {
        var (groupId, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares((Self, 1), (other, 1)));

        // Paid by the member who is about to leave.
        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 100.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            PaidByUserId = other
        }, Ct);

        await Departs(groupId, other);

        // What the edit dialog sends: the new amount, with the shares as they stood. They no
        // longer add up, but that is not the reason -- the leaver's part is the amount itself.
        var refused = await Assert.ThrowsAsync<ConflictException>(() => Transactions.Update(expense.Id,
            new UpdateTransactionRequest
            {
                Name = "Dinner",
                Amount = 120.00m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserId = other,
                GroupId = groupId,
                CategoryId = categoryId,
                Splits =
                [
                    new SplitInput { UserId = Self, Amount = 50.00m },
                    new SplitInput { UserId = other, Amount = 50.00m }
                ]
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.TransactionMovesDepartedBalance, refused.Code);
        Assert.Equal([other], Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["userIds"]));

        DbContext.ChangeTracker.Clear();

        Assert.Equal(100.00m, (await DbContext.Set<Expense>().AsNoTracking()
            .FirstAsync(candidate => candidate.Id == expense.Id, Ct)).Amount);
    }

    [Fact]
    public async Task Re_dividing_an_old_expense_a_member_who_has_left_shared_is_refused()
    {
        var (groupId, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares((Self, 1), (other, 1)));

        var expense = await AnExpense(groupId, categoryId, 200.00m);

        Assert.Equal(100.00m, (await SplitsOf(expense.Id)).Single(split => split.UserId == other).Amount);

        await Departs(groupId, other);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            Redivide(expense.Id, groupId, categoryId, 300.00m).AsTask());

        Assert.Equal(ErrorCodes.TransactionMovesDepartedBalance, refused.Code);
        Assert.Equal([other], Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["userIds"]));

        DbContext.ChangeTracker.Clear();

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(100.00m, splits.Single(split => split.UserId == other).Amount);
        Assert.Equal(200.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// Once somebody has gone, a new expense filed under the same category divides between
    /// the people who are here: the pruned version names only them, so every share belongs
    /// to somebody the group's balances list.
    /// </summary>
    /// <remarks>
    /// The property underneath "the balances sum to zero". It used to be checked on an old
    /// expense divided again, with a remark that zero itself could not be asserted after a
    /// departure, because dividing again moved the leaver's position. Nothing can move it
    /// now, so zero is asserted here as it is everywhere else.
    /// </remarks>
    [Fact]
    public async Task Every_share_of_an_expense_after_a_departure_belongs_to_somebody_the_group_lists()
    {
        var (groupId, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares((Self, 1), (other, 1)));

        await AnExpense(groupId, categoryId, 200.00m);

        await AssertBalancesSumToZero(groupId);

        await Departs(groupId, other);

        var later = await AnExpense(groupId, categoryId, 300.00m);

        var listed = (await Balances(groupId)).Select(balance => balance.UserId).ToHashSet();

        Assert.DoesNotContain(other, listed);

        var splits = await SplitsOf(later.Id);

        Assert.All(splits, split => Assert.Contains(split.UserId, listed));
        Assert.Equal(300.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A version naming nobody who is left divides between nobody, and that is a refusal in
    /// words naming the rule rather than a 500.
    /// </summary>
    /// <remarks>
    /// The path <c>ExpenseSplitter.DividedByRule</c> already had for a rule emptied by a
    /// departure. Trimming the version's participants reaches it from a second direction --
    /// a version that still names people, none of whom are here -- and it has to go on
    /// reading as the same answer: which rule, and what to do about it.
    /// </remarks>
    [Fact]
    public async Task A_version_naming_only_people_who_have_gone_is_refused_by_name()
    {
        var (groupId, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Theirs", Shares((other, 1)));

        var expense = await AnExpense(groupId, categoryId, 200.00m);

        await Departs(groupId, other);

        var refused = await Assert.ThrowsAsync<ValidationException>(
            () => Redivide(expense.Id, groupId, categoryId, 200.00m).AsTask());

        Assert.Equal(ErrorCodes.SplitRuleInvalid, refused.Code);
        Assert.Contains("Theirs", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Somebody invited and still to answer is not somebody who has gone: they keep their
    /// weight, because a share against an unanswered invitation is an ordinary share.
    /// </summary>
    [Fact]
    public async Task Dividing_again_still_weighs_a_participant_who_has_not_joined_yet()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var invitation = (await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Dani"] }, Ct)).Single();

        var categoryId = await CreateCategory(group.Id, "Rent",
            Shares((Self, 1), (invitation.ParticipantUserId, 1)));

        var expense = await AnExpense(group.Id, categoryId, 200.00m);

        await Redivide(expense.Id, group.Id, categoryId, 300.00m);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(150.00m,
            splits.Single(split => split.UserId == invitation.ParticipantUserId).Amount);

        await AssertBalancesSumToZero(group.Id);
    }

    /// <summary>
    /// A rule that puts the whole amount on a guest follows them to whoever claims them --
    /// which is what the claim has just done with their shares and the expenses they were
    /// down as having paid for.
    /// </summary>
    [Fact]
    public async Task A_rule_naming_a_guest_names_whoever_claims_them()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var invitation = (await Invitations.Invite(group.Id,
            new InviteToGroupRequest { Names = ["Dani"] }, Ct)).Single();

        var rule = await GetService<ISplitRuleService>().Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Dani's gym",
            Definition = new SoleSplitRuleDto(invitation.ParticipantUserId)
        }, Ct);

        // Claimed by the member who made the link, who turns out to be Dani.
        await Invitations.Claim(invitation.Token, Ct);

        var versions = await DbContext.Set<SplitRuleVersion>()
            .AsNoTracking()
            .Where(version => version.SplitRuleId == rule.Id)
            .ToListAsync(Ct);

        var only = Assert.IsType<SoleSplitRuleVersion>(Assert.Single(versions));

        Assert.Equal(Self, only.UserId);
        Assert.Null(only.SupersededAt);
    }
}

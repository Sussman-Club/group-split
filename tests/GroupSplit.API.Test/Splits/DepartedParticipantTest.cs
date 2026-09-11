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

        await Groups.DetachMember(group, user, Ct);
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
    [Fact]
    public async Task Re_dividing_an_old_expense_gives_a_member_who_has_left_nothing()
    {
        var (groupId, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares((Self, 1), (other, 1)));

        var expense = await AnExpense(groupId, categoryId, 200.00m);

        Assert.Equal(100.00m, (await SplitsOf(expense.Id)).Single(split => split.UserId == other).Amount);

        await Departs(groupId, other);

        await Redivide(expense.Id, groupId, categoryId, 300.00m);

        var splits = await SplitsOf(expense.Id);

        // The version this expense holds is superseded and still names them -- that is what
        // makes it readable at all -- but the division is between the people who are here.
        Assert.DoesNotContain(splits, split => split.UserId == other);
        Assert.Equal(300.00m, Assert.Single(splits).Amount);
        Assert.Equal(Self, splits[0].UserId);
    }

    /// <summary>
    /// And every share the re-divided expense holds belongs to somebody the group's
    /// balances list.
    /// </summary>
    /// <remarks>
    /// The property underneath "the balances sum to zero", and the one that survives a
    /// departure. A group's column adds up because every share belongs to a name on the page
    /// -- <c>GroupService.NetBalances</c> says so where it chooses participants over members
    /// -- and a division paying somebody who has gone puts a share outside the listing
    /// entirely, which is the shape the column comes apart in.
    /// <para>
    /// Zero itself is not assertable here, and not because of this: a member who settles and
    /// leaves keeps the transactions they paid for, so re-dividing an expense they held a
    /// share of moves their position whichever way it is divided -- to nothing, with this,
    /// and to a different number without it. The invariant that can be held is the one
    /// checked below, and the tests that do assert zero are the ones where nobody has left.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_share_of_a_re_divided_expense_belongs_to_somebody_the_group_lists()
    {
        var (groupId, other) = await GroupOfTwo();
        var categoryId = await CreateCategory(groupId, "Rent", Shares((Self, 1), (other, 1)));

        var expense = await AnExpense(groupId, categoryId, 200.00m);

        await AssertBalancesSumToZero(groupId);

        await Departs(groupId, other);

        await Redivide(expense.Id, groupId, categoryId, 300.00m);

        var listed = (await Balances(groupId)).Select(balance => balance.UserId).ToHashSet();

        Assert.DoesNotContain(other, listed);

        var splits = await SplitsOf(expense.Id);

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
}

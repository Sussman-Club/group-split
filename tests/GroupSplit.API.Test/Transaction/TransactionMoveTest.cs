using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Moving an expense between the personal ledger and a group, and between groups.
/// </summary>
/// <remarks>
/// The primitive the bank-sync review inbox is built on: an imported transaction lands as
/// the account holder's own, and "share this one with the flat" is a move into a group.
/// The other direction is the correction -- "that was mine, not ours". A move re-derives
/// the division among the destination's members, because the old shares name people who
/// may not be in it, and takes the destination's currency.
/// </remarks>
public class TransactionMoveTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private ITransactionService Transactions => GetService<ITransactionService>();
    private IGroupService Groups => GetService<IGroupService>();
    private Guid Me => GetService<ICurrentUser>().User.Id;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid GroupId, Guid Other)> AGroupOfTwo(string name = "Flat")
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);
        return (group.Id, other.Id);
    }

    private Task<Expense> APersonalExpense(decimal amount = 30m) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Groceries",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow
        }, Ct).AsTask();

    private Task<List<TransactionSplit>> SplitsOf(Guid expenseId) =>
        DbContext.Set<TransactionSplit>().Where(split => split.TransactionId == expenseId).ToListAsync(Ct);

    private UpdateTransactionRequest Moving(Expense expense, Guid? toGroup, Guid? category = null) => new()
    {
        Name = expense.Name,
        Amount = expense.Amount,
        DateTime = expense.DateTime,
        PaidByUserId = Me,
        GroupId = toGroup,
        CategoryId = category
    };

    [Fact]
    public async Task A_personal_expense_moved_into_a_group_is_divided_among_its_members()
    {
        var (groupId, other) = await AGroupOfTwo();
        var expense = await APersonalExpense(30m);

        var moved = await Transactions.Update(expense.Id, Moving(expense, groupId), Ct);

        Assert.Equal(groupId, moved.GroupId);

        var splits = await SplitsOf(expense.Id);
        Assert.Equal(2, splits.Count);
        Assert.Equal(30m, splits.Sum(split => split.Amount));
        Assert.Contains(splits, split => split.UserId == other && split.Amount == 15m);
    }

    [Fact]
    public async Task A_group_expense_taken_personal_has_one_share_and_no_category()
    {
        var (groupId, _) = await AGroupOfTwo();
        var categoryId = await CreateEvenCategory(groupId, "Food");

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId
        }, Ct);

        var moved = await Transactions.Update(expense.Id, Moving(expense, toGroup: null), Ct);

        Assert.Null(moved.GroupId);
        Assert.Null((moved as Expense)?.CategoryId);

        var share = Assert.Single(await SplitsOf(expense.Id));
        Assert.Equal(Me, share.UserId);
        Assert.Equal(40m, share.Amount);
    }

    /// <summary>
    /// Only your own expense can be made personal. Taking somebody else's out of the group
    /// would delete a debt they are owed, from under them.
    /// </summary>
    [Fact]
    public async Task Somebody_elses_expense_cannot_be_taken_personal()
    {
        var (groupId, other) = await AGroupOfTwo();

        var theirs = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Their dinner",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = other
        }, Ct);

        var request = Moving(theirs, toGroup: null) with { PaidByUserId = other };

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Transactions.Update(theirs.Id, request, Ct).AsTask());

        Assert.Equal(ErrorCodes.TransactionPayerNotInGroup, refusal.Code);
    }

    [Fact]
    public async Task Moving_to_a_group_the_caller_is_not_in_is_a_404()
    {
        var expense = await APersonalExpense();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Transactions.Update(expense.Id, Moving(expense, Guid.NewGuid()), Ct).AsTask());
    }

    /// <summary>A category belongs to a group, so it cannot follow the expense to another.</summary>
    [Fact]
    public async Task A_category_from_the_old_group_does_not_come_along()
    {
        var (from, _) = await AGroupOfTwo("From");
        var (to, _) = await AGroupOfTwo("To");
        var oldCategory = await CreateEvenCategory(from, "Food");

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = from,
            CategoryId = oldCategory
        }, Ct);

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() =>
            Transactions.Update(expense.Id, Moving(expense, to, oldCategory), Ct).AsTask());

        Assert.Equal(ErrorCodes.CategoryNotFound, refusal.Code);

        // Without the category it goes, and is divided among the new group.
        var moved = await Transactions.Update(expense.Id, Moving(expense, to), Ct);

        Assert.Equal(to, moved.GroupId);
        Assert.Equal(2, (await SplitsOf(expense.Id)).Count);
    }

    /// <summary>Stated shares have to name the destination's members, not the origin's.</summary>
    [Fact]
    public async Task Stated_shares_naming_the_old_groups_members_are_refused()
    {
        var (from, oldOther) = await AGroupOfTwo("From");
        var (to, _) = await AGroupOfTwo("To");

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = from
        }, Ct);

        var request = Moving(expense, to) with
        {
            // Not the even 10/10 the expense already holds: shares identical to the stored
            // ones read as silence, and silence on a move asks for the destination's own
            // division. These are somebody naming the old group's members on purpose.
            Splits =
            [
                new SplitInput { UserId = Me, Amount = 15m },
                new SplitInput { UserId = oldOther, Amount = 5m }
            ]
        };

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            Transactions.Update(expense.Id, request, Ct).AsTask());

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, refusal.Code);
    }

    /// <summary>
    /// The PATCH path pre-fills the model with the expense's current group, so a patch that
    /// says nothing about the group leaves it where it is.
    /// </summary>
    [Fact]
    public async Task The_update_model_carries_the_current_group_so_silence_keeps_it()
    {
        var (groupId, _) = await AGroupOfTwo();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId
        }, Ct);

        var model = await Transactions.GetUpdateModel(expense.Id, Ct);

        Assert.Equal(groupId, model!.GroupId);
    }
}

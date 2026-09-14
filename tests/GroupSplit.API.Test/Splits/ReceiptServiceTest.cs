using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// What a bill refuses, and what it leaves alone when it does.
/// </summary>
/// <remarks>
/// <see cref="ItemizedRuleTest"/> is about the division a bill produces. This is about the
/// answers around it -- whose expense may carry one, who may still change it, and what
/// happens to the expense when the bill goes -- each of which the service spells out in
/// prose and none of which the division tests reach.
/// </remarks>
public class ReceiptServiceTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IReceiptService Receipts => GetService<IReceiptService>();
    private ISplitRuleService Rules => GetService<ISplitRuleService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private IGroupService Groups => GetService<IGroupService>();
    private Guid Self => GetService<ICurrentUser>().User.Id;

    private static ReceiptItemInput Item(string name, decimal price, Guid? rule, Guid? id = null) =>
        new() { Id = id, Name = name, UnitPrice = price, TotalPrice = price, SplitRuleVersionId = rule };

    private static SaveReceiptRequest Bill(params ReceiptItemInput[] items) => new()
    {
        Items = items, Subtotal = items.Sum(i => i.TotalPrice), Tax = 0m,
        Total = items.Sum(i => i.TotalPrice)
    };

    /// <summary>A group of two, an expense of 48 in it, and a rule its lines can name.</summary>
    private async Task<(Expense Expense, Guid Group, Guid Even)> AnExpense(SplitRuleDto? divides = null)
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Dinner" }, Ct);
        await JoinGroup(group.Id, await CreateNewUser());
        var category = await CreateCategory(group.Id, "Food", divides ?? new ItemizedSplitRuleDto());
        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner", Amount = 48m, DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id, CategoryId = category,
            Splits = [new SplitInput { UserId = Self, Amount = 48m }]
        }, Ct);
        var even = await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group.Id, Name = "Together", Definition = new EvenSplitRuleDto() }, Ct);
        return (expense, group.Id, even.Current!.Id);
    }

    /// <summary>
    /// A personal expense is one person's money, so there is nobody to divide its lines
    /// between and the bill is refused before anything is stored.
    /// </summary>
    [Fact]
    public async Task A_personal_expense_has_nobody_to_split_a_bill_with()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Lunch", Amount = 10m, DateTime = DateTimeOffset.UtcNow }, Ct);

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Receipts.SaveForExpense(expense.Id, Bill(Item("Sandwich", 10m, null)), Ct));

        Assert.Equal(ErrorCodes.SplitOnAPersonalExpense, refusal.Code);
    }

    /// <summary>
    /// Somebody who has left keeps reading the expense they paid for -- it is their own
    /// record -- and stops being able to move the group's balances with it. The flag on the
    /// response says the same thing before they try.
    /// </summary>
    [Fact]
    public async Task A_leaver_still_reads_the_bill_and_can_no_longer_change_it()
    {
        var (expense, group, even) = await AnExpense();
        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48m, even)), Ct);
        var line = bill.Items.Single().Id;

        await Groups.Leave(group, Ct);

        Assert.Equal(48m, (await Receipts.ForExpense(expense.Id, Ct)).Total);
        Assert.False((await Receipts.ResponseFor(bill, ct: Ct)).CanDivide);
        Assert.False((await Receipts.ResponseFor(bill, ct: Ct)).CanEdit);

        foreach (var refused in new Func<Task>[]
                 {
                     () => Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48m, even)), Ct),
                     () => Receipts.SetRule(expense.Id, line, new SetReceiptItemRuleRequest(), Ct),
                     () => Receipts.Divide(expense.Id, Ct),
                     () => Receipts.DeleteForExpense(expense.Id, Ct)
                 })
        {
            var refusal = await Assert.ThrowsAsync<ConflictException>(() => refused());
            Assert.Equal(ErrorCodes.TransactionGroupLeft, refusal.Code);
        }
    }

    /// <summary>
    /// An id on a saved line says "this is that line, corrected". One that names a line of
    /// another bill, or names the same line twice, says nothing of the sort.
    /// </summary>
    [Fact]
    public async Task A_line_id_from_another_bill_or_named_twice_is_refused()
    {
        var (mine, _, even) = await AnExpense();
        var (other, _, otherRule) = await AnExpense();
        var elsewhere = (await Receipts.SaveForExpense(other.Id,
            Bill(Item("Dinner", 48m, otherRule)), Ct)).Items.Single().Id;
        var bill = await Receipts.SaveForExpense(mine.Id, Bill(Item("Dinner", 48m, even)), Ct);
        var line = bill.Items.Single().Id;

        await Assert.ThrowsAsync<ValidationException>(() => Receipts.SaveForExpense(mine.Id,
            Bill(Item("Dinner", 48m, even, elsewhere)), Ct));
        await Assert.ThrowsAsync<ValidationException>(() => Receipts.SaveForExpense(mine.Id,
            Bill(Item("Half", 24m, even, line), Item("Half again", 24m, even, line)), Ct));

        Assert.Equal("Dinner", (await Receipts.ForExpense(mine.Id, Ct)).Items.Single().Name);
    }

    /// <summary>A line that is not on the bill is not found, rather than quietly ignored.</summary>
    [Fact]
    public async Task A_rule_set_on_a_line_that_is_not_on_the_bill_is_not_found()
    {
        var (expense, _, even) = await AnExpense();
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48m, even)), Ct);

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() => Receipts.SetRule(
            expense.Id, Guid.NewGuid(), new SetReceiptItemRuleRequest { SplitRuleVersionId = even }, Ct));

        Assert.Equal(ErrorCodes.ReceiptItemNotFound, refusal.Code);
    }

    /// <summary>
    /// Deleting the bill takes the paper and not the money: the expense and the division it
    /// was last written with are still there, and it simply has no bill again.
    /// </summary>
    [Fact]
    public async Task Deleting_the_bill_leaves_the_expense_and_its_division()
    {
        var (expense, _, even) = await AnExpense();
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48m, even)), Ct);
        await Receipts.Divide(expense.Id, Ct);
        var divided = expense.Splits.ToDictionary(split => split.UserId, split => split.Amount);

        await Receipts.DeleteForExpense(expense.Id, Ct);

        var refusal = await Assert.ThrowsAsync<NotFoundException>(() => Receipts.ForExpense(expense.Id, Ct));
        Assert.Equal(ErrorCodes.ReceiptNotFound, refusal.Code);

        var details = await Transactions.GetDetails(expense.Id, Ct);
        Assert.Equal(48m, details!.Amount);
        Assert.Equal(divided, details.Splits.ToDictionary(split => split.UserId, split => split.Amount));
    }

    /// <summary>
    /// A bill under a category that divides some other way is still divisible by its lines,
    /// and the shares it produces are written as amounts somebody chose -- the expense's own
    /// rule is not itemized, and dividing by that rule again would undo them.
    /// </summary>
    [Fact]
    public async Task A_bill_on_an_expense_that_divides_another_way_is_written_as_stated_shares()
    {
        var (expense, _, even) = await AnExpense(new EvenSplitRuleDto());
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48m, even)), Ct);
        var preview = await Receipts.Preview(expense.Id, Ct);

        await Receipts.Divide(expense.Id, Ct);

        Assert.Equal(preview.Shares.ToDictionary(share => share.UserId, share => share.Amount),
            expense.Splits.ToDictionary(split => split.UserId, split => split.Amount));
        Assert.Equal(48m, expense.Splits.Sum(split => split.Amount));
    }
}

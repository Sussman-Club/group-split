using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// What a bill line starts on when it arrives without a rule: the category's default, then
/// the group's default split, then Even -- the first a line can use.
/// </summary>
/// <remarks>
/// Most of a bill divides the way the group always divides that kind of expense, so only the
/// exceptions should need a rule picked. And a pre-fill is a starting point, never an
/// overwrite: a line that came with a rule, or a line already on the bill, keeps what it has.
/// </remarks>
public class BillLineDefaultTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IReceiptService Receipts => GetService<IReceiptService>();
    private ISplitRuleService Rules => GetService<ISplitRuleService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private Guid Self => GetService<ICurrentUser>().User.Id;

    private static ReceiptItemInput Item(string name, decimal price, Guid? rule = null, Guid? id = null) =>
        new() { Id = id, Name = name, UnitPrice = price, TotalPrice = price, SplitRuleVersionId = rule };

    private static SaveReceiptRequest Bill(params ReceiptItemInput[] items) => new()
    {
        Items = items, Subtotal = items.Sum(i => i.TotalPrice), Total = items.Sum(i => i.TotalPrice)
    };

    /// <summary>A group of two: the caller and somebody else.</summary>
    private async Task<(Guid Group, Guid Other)> AGroupOfTwo()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);
        return (group.Id, other.Id);
    }

    private async Task<(Guid Rule, Guid Version)> ARule(Guid group, string name, SplitRuleDto definition)
    {
        var rule = await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group, Name = name, Definition = definition }, Ct);
        return (rule.Id, rule.Current!.Id);
    }

    private async Task<Guid> ACategory(Guid group, string name, Guid rule) =>
        (await GetService<ICategoryService>().Create(new CreateCategoryRequest
            { GroupId = group, Name = name, DefaultSplitRuleId = rule }, Ct)).Id;

    /// <summary>An expense of 30 in the group, divided by hand so its category moves nothing.</summary>
    private async Task<Expense> AnExpense(Guid group, Guid? category) =>
        await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Shopping", Amount = 30m, DateTime = DateTimeOffset.UtcNow, GroupId = group,
            CategoryId = category, Splits = [new SplitInput { UserId = Self, Amount = 30m }]
        }, Ct);

    /// <summary>Points the group's default split somewhere other than the Even it is given.</summary>
    private async Task<Guid> AGroupDefault(Guid group, SplitRuleDto definition)
    {
        var (rule, version) = await ARule(group, "Mostly mine", definition);
        var stored = await DbContext.Set<Data.Entities.Group>().FirstAsync(g => g.Id == group, Ct);
        stored.DefaultSplitRuleId = rule;
        await DbContext.SaveChangesAsync(Ct);
        return version;
    }

    private async Task<Guid> EvenOf(Guid group) =>
        (await GetService<IEvenSplitRule>().For(group, Ct))!.Current!.Id;

    private async Task Leaves(Guid group, Guid user)
    {
        var membership = await DbContext.Set<GroupMembership>()
            .FirstAsync(row => row.GroupId == group && row.UserId == user, Ct);
        membership.LeftAt = DateTimeOffset.UtcNow;
        await DbContext.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Every_line_starts_on_the_categorys_rule_and_the_bill_can_divide()
    {
        var (group, other) = await AGroupOfTwo();
        var (rule, groceries) = await ARule(group, "Groceries split",
            new SharesSplitRuleDto { Shares = new() { [Self] = 2, [other] = 1 } });
        var expense = await AnExpense(group, await ACategory(group, "Groceries", rule));

        var bill = await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Bread", 6m), Item("Milk", 9m), Item("Apples", 15m)), Ct);
        var response = await Receipts.ResponseFor(bill, ct: Ct);

        Assert.All(bill.Items, item => Assert.Equal(groceries, item.SplitRuleVersionId));
        Assert.Equal(0, response.MissingRuleItemCount);
        Assert.True(response.CanDivide);
    }

    /// <summary>
    /// "Dinners out" may well divide by the bill, which a line cannot -- so this is the usual
    /// reason to skip a step, not an edge case.
    /// </summary>
    [Fact]
    public async Task Under_a_category_that_divides_by_the_bill_lines_start_on_the_group_default()
    {
        var (group, other) = await AGroupOfTwo();
        var mostlyMine = await AGroupDefault(group, new SharesSplitRuleDto { Shares = new() { [Self] = 3, [other] = 1 } });
        var expense = await AnExpense(group, await CreateCategory(group, "Dinners out", new ItemizedSplitRuleDto()));

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Pasta", 30m)), Ct);

        Assert.Equal(mostlyMine, Assert.Single(bill.Items).SplitRuleVersionId);
    }

    [Fact]
    public async Task Under_no_category_lines_start_on_the_group_default()
    {
        var (group, other) = await AGroupOfTwo();
        var mostlyMine = await AGroupDefault(group, new SharesSplitRuleDto { Shares = new() { [Self] = 3, [other] = 1 } });
        var expense = await AnExpense(group, null);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Pasta", 30m)), Ct);

        Assert.Equal(mostlyMine, Assert.Single(bill.Items).SplitRuleVersionId);
    }

    /// <summary>
    /// A group is given Even as its default split, so with nothing else said that is where a
    /// line starts.
    /// </summary>
    [Fact]
    public async Task A_group_left_on_its_own_default_starts_lines_on_even()
    {
        var (group, _) = await AGroupOfTwo();
        var expense = await AnExpense(group, null);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Pasta", 30m)), Ct);

        Assert.Equal(await EvenOf(group), Assert.Single(bill.Items).SplitRuleVersionId);
    }

    /// <summary>
    /// A rule naming somebody who has left would move what they owed, so a line cannot use
    /// it: the next step is tried, down to Even, which names nobody.
    /// </summary>
    [Fact]
    public async Task A_rule_naming_somebody_who_left_is_passed_over_down_to_even()
    {
        var (group, other) = await AGroupOfTwo();
        var both = new SharesSplitRuleDto { Shares = new() { [Self] = 1, [other] = 1 } };
        var (rule, _) = await ARule(group, "Groceries split", both);
        var category = await ACategory(group, "Groceries", rule);
        var mostlyMine = await AGroupDefault(group, new SharesSplitRuleDto { Shares = new() { [Self] = 3, [other] = 1 } });
        var expense = await AnExpense(group, category);

        await Leaves(group, other);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Pasta", 30m)), Ct);

        // The category's rule and the group's default both name them; Even is all that is left.
        Assert.NotEqual(mostlyMine, Assert.Single(bill.Items).SplitRuleVersionId);
        Assert.Equal(await EvenOf(group), bill.Items.Single().SplitRuleVersionId);
    }

    [Fact]
    public async Task A_category_rule_naming_somebody_who_left_falls_to_the_group_default()
    {
        var (group, other) = await AGroupOfTwo();
        var (rule, _) = await ARule(group, "Theirs", new SoleSplitRuleDto(other));
        var expense = await AnExpense(group, await ACategory(group, "Their stuff", rule));

        await Leaves(group, other);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Pasta", 30m)), Ct);

        Assert.Equal(await EvenOf(group), Assert.Single(bill.Items).SplitRuleVersionId);
    }

    /// <summary>
    /// A rule sent with a line is that line's, whatever the category says; and a line already
    /// on the bill keeps what it has, even no rule, which somebody may have chosen by
    /// clearing it.
    /// </summary>
    [Fact]
    public async Task A_rule_that_was_given_is_never_replaced()
    {
        var (group, other) = await AGroupOfTwo();
        var (rule, groceries) = await ARule(group, "Groceries split",
            new SharesSplitRuleDto { Shares = new() { [Self] = 2, [other] = 1 } });
        var (_, theirs) = await ARule(group, "Theirs", new SoleSplitRuleDto(other));
        var expense = await AnExpense(group, await ACategory(group, "Groceries", rule));

        var bill = await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Steak", 20m, theirs), Item("Bread", 10m)), Ct);
        var steak = bill.Items.Single(item => item.Name == "Steak");
        var bread = bill.Items.Single(item => item.Name == "Bread");
        Assert.Equal(theirs, steak.SplitRuleVersionId);
        Assert.Equal(groceries, bread.SplitRuleVersionId);

        await Receipts.SetRule(expense.Id, bread.Id, new SetReceiptItemRuleRequest(), Ct);
        var saved = await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Steak", 20m, theirs, steak.Id), Item("Bread", 10m, id: bread.Id)), Ct);

        Assert.Equal(theirs, saved.Items.Single(item => item.Id == steak.Id).SplitRuleVersionId);
        Assert.Null(saved.Items.Single(item => item.Id == bread.Id).SplitRuleVersionId);
    }

    /// <summary>
    /// A line added to a bill that already exists starts the same way, and the lines already
    /// there are left exactly as they were.
    /// </summary>
    [Fact]
    public async Task A_line_added_to_an_existing_bill_starts_on_the_default()
    {
        var (group, other) = await AGroupOfTwo();
        var (rule, groceries) = await ARule(group, "Groceries split",
            new SharesSplitRuleDto { Shares = new() { [Self] = 2, [other] = 1 } });
        var (_, theirs) = await ARule(group, "Theirs", new SoleSplitRuleDto(other));
        var expense = await AnExpense(group, await ACategory(group, "Groceries", rule));
        var first = Assert.Single((await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Steak", 30m, theirs)), Ct)).Items);

        var bill = await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Steak", 20m, theirs, first.Id), Item("Wine", 10m)), Ct);

        Assert.Equal(theirs, bill.Items.Single(item => item.Id == first.Id).SplitRuleVersionId);
        Assert.Equal(groceries, bill.Items.Single(item => item.Name == "Wine").SplitRuleVersionId);
    }

    /// <summary>
    /// The pre-fill is only where lines start. Moving one leaves the others where they are,
    /// and the shares still come to what was paid.
    /// </summary>
    [Fact]
    public async Task Moving_one_line_leaves_the_others_and_the_bill_still_divides_exactly()
    {
        var (group, other) = await AGroupOfTwo();
        var (rule, groceries) = await ARule(group, "Groceries split", new EvenSplitRuleDto());
        var (_, theirs) = await ARule(group, "Theirs", new SoleSplitRuleDto(other));
        var expense = await AnExpense(group, await ACategory(group, "Groceries", rule));
        var bill = await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Bread", 10m), Item("Steak", 10m), Item("Milk", 10m)), Ct);
        var steak = bill.Items.Single(item => item.Name == "Steak").Id;

        await Receipts.SetRule(expense.Id, steak, new SetReceiptItemRuleRequest { SplitRuleVersionId = theirs }, Ct);
        var preview = await Receipts.Preview(expense.Id, Ct);

        var lines = (await Receipts.ForExpense(expense.Id, Ct)).Items;
        Assert.Equal(theirs, lines.Single(item => item.Id == steak).SplitRuleVersionId);
        Assert.All(lines.Where(item => item.Id != steak), item => Assert.Equal(groceries, item.SplitRuleVersionId));
        Assert.Equal(30m, preview.Shares.Sum(share => share.Amount));
        Assert.Equal(20m, preview.Shares.Single(share => share.UserId == other).Amount);
    }

    /// <summary>A personal expense has no group to divide with, so its lines take no rule.</summary>
    [Fact]
    public async Task A_personal_expenses_lines_start_on_no_rule()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Lunch", Amount = 10m, DateTime = DateTimeOffset.UtcNow }, Ct);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Sandwich", 10m)), Ct);

        Assert.Null(Assert.Single(bill.Items).SplitRuleVersionId);
    }
}

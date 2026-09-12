using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// A category that divides by the bill.
/// </summary>
/// <remarks>
/// The first rule kind whose answer is not on the rule. Everything else a version says, it
/// carries; this one reads the receipt attached to whichever expense is being divided, which
/// is why <c>ISplitRuleHandler.Divide</c> takes the transaction at all.
/// <para>
/// What that buys is provenance an ad-hoc division cannot have: the expense records which
/// version divided it, so an edit to the amount is worked out again from the bill rather
/// than refused. What it costs is the states below -- an expense filed here with no bill, or
/// with a line nobody has claimed -- which are ordinary and must not turn an unrelated edit
/// into an error.
/// </para>
/// </remarks>
public class ItemizedRuleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupService Groups => GetService<IGroupService>();

    private ITransactionService Transactions => GetService<ITransactionService>();

    private ICategoryService Categories => GetService<ICategoryService>();

    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    private async Task<(Guid GroupId, Guid Other, Guid CategoryId)> ItemizedGroup()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "By the bill",
            Definition = new ItemizedSplitRuleDto()
        }, Ct);

        var category = await Categories.Create(new CreateCategoryRequest
        {
            GroupId = group.Id,
            Name = "Dinners",
            DefaultSplitRuleId = rule.Id
        }, Ct);

        return (group.Id, other.Id, category.Id);
    }

    /// <summary>
    /// The bill has to be on the expense before the division reads it, and an expense cannot
    /// carry one until it exists -- so the receipt is attached to the tracked entity and the
    /// division asked for again, which is what the endpoint's own divide call does.
    /// </summary>
    private async Task<Receipt> Bill(
        Guid expenseId, decimal tax, decimal tip,
        params (string Name, decimal Price, Guid[] Had)[] lines)
    {
        var receipt = new Receipt
        {
            Subtotal = lines.Sum(line => line.Price),
            Tax = tax,
            Tip = tip,
            Total = lines.Sum(line => line.Price) + tax + tip
        };

        foreach (var (name, price, had) in lines)
        {
            var item = new ReceiptItem
            {
                ReceiptId = receipt.Id,
                ExpenseId = expenseId,
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                TotalPrice = price
            };

            foreach (var userId in had)
                item.Claims.Add(new ReceiptItemClaim { UserId = userId, Weight = 1 });

            receipt.Items.Add(item);
        }

        DbContext.Add(receipt);
        await DbContext.SaveChangesAsync(Ct);

        return receipt;
    }

    private Task<Expense> AnExpense(Guid groupId, Guid categoryId, decimal amount) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            // Stated, so that creating it does not try to read a bill that cannot exist yet.
            // The real flow is the same shape: the expense is recorded, then itemised.
            Splits = [new SplitInput { UserId = Self, Amount = amount }]
        }, Ct).AsTask();

    /// <summary>
    /// Takes a member out the way an account deletion does, without the endpoint's settled-up
    /// check standing in the way of a test that is about the division.
    /// </summary>
    private async Task Departs(Guid groupId, Guid userId)
    {
        var group = await DbContext.Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == groupId, Ct);

        var user = await DbContext.Set<Data.Entities.User>().FirstAsync(candidate => candidate.Id == userId, Ct);

        await Groups.DetachMember(group, user, Ct);
        await DbContext.SaveChangesAsync(Ct);
    }

    private Task<List<TransactionSplit>> SplitsOf(Guid id) =>
        DbContext.Set<TransactionSplit>().AsNoTracking()
            .Where(split => split.TransactionId == id).ToListAsync(Ct);

    [Fact]
    public async Task An_expense_is_divided_by_its_bill_and_records_the_version_that_did_it()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 120.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 120.00m }]
        }, Ct);

        // Three quarters of the food is mine, so three quarters of the 20.00 that is not
        // food is mine too -- which an even split of the extras would have got wrong.
        await Bill(expense.Id, tax: 8.00m, tip: 12.00m,
            ("Steak", 75.00m, [Self]),
            ("Pasta", 25.00m, [other]));

        // The wiring the division rests on, asserted rather than assumed: the category names
        // the rule, and the rule is on an itemised version. Both were worth pinning down --
        // an unregistered derived type is not a mapping error in EF, it is a row stored and
        // read back as its base class, which reaches the dispatcher as the wrong kind and
        // divides as though the rule said something else entirely.
        var cat = await DbContext.Set<Category>().AsNoTracking()
            .FirstAsync(c => c.Id == categoryId, Ct);
        Assert.NotNull(cat.DefaultSplitRuleId);

        var ver = await DbContext.Set<SplitRuleVersion>().AsNoTracking()
            .FirstOrDefaultAsync(v => v.SplitRuleId == cat.DefaultSplitRuleId && v.SupersededAt == null, Ct);
        Assert.IsType<ItemizedSplitRuleVersion>(ver);

        await GetService<IReceiptService>().Divide(expense.Id, Ct);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(90.00m, splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(30.00m, splits.Single(split => split.UserId == other).Amount);

        // The point of it being a rule: the expense says which division produced its shares,
        // so a later edit is worked out again from the bill rather than refused.
        var stored = await DbContext.Set<Expense>().AsNoTracking()
            .FirstAsync(candidate => candidate.Id == expense.Id, Ct);

        Assert.NotNull(stored.SplitRuleVersionId);
    }

    /// <summary>
    /// The regression that matters most, because it is on the path of every edit rather than
    /// of the division: <c>DivisionCameFromItsRule</c> re-runs the division to decide whether
    /// to leave stored shares alone, and an itemised rule can refuse to divide at all.
    /// </summary>
    [Fact]
    public async Task Renaming_an_expense_whose_bill_is_not_fully_claimed_does_not_fail()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 60.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = Self, Amount = 30.00m },
                new SplitInput { UserId = other, Amount = 30.00m }
            ]
        }, Ct);

        // A line nobody has claimed: the ordinary state of a bill somebody is still working
        // through, and one the division refuses.
        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Shared plate", 40.00m, [Self, other]),
            ("Mystery line", 20.00m, []));

        var renamed = await Transactions.Update(expense.Id, new UpdateTransactionRequest
        {
            Name = "Dinner at the Italian place",
            Amount = 60.00m,
            DateTime = expense.DateTime,
            PaidByUserId = Self,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = Self, Amount = 30.00m },
                new SplitInput { UserId = other, Amount = 30.00m }
            ]
        }, Ct);

        Assert.Equal("Dinner at the Italian place", renamed.Name);

        // And the shares nobody asked to change are exactly as they were.
        var splits = await SplitsOf(expense.Id);

        Assert.Equal(30.00m, splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(30.00m, splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// The invariant every balance rests on, on the path that could break it.
    /// </summary>
    /// <remarks>
    /// An itemised rule is the first kind that divides something other than the transaction's
    /// amount -- it divides the receipt's total -- so it is the first that can hand
    /// ExpenseSplitter a set of shares that does not sum to the expense. Linking a bank row
    /// to an expense recorded for a different figure is how the two drift apart in practice.
    /// </remarks>
    [Fact]
    public async Task A_bill_whose_total_is_not_the_expense_amount_is_refused_rather_than_stored()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 95.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 95.00m }]
        }, Ct);

        // A 100.00 bill on a 95.00 expense. Nothing in ReceiptService wrote this -- it is the
        // shape linking leaves behind -- so the splitter is the only thing that can catch it.
        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 60.00m, [Self]),
            ("Pasta", 40.00m, [other]));

        await Assert.ThrowsAsync<UnprocessableException>(
            () => GetService<IReceiptService>().Divide(expense.Id, Ct));

        // And the shares it already held are untouched.
        var splits = await SplitsOf(expense.Id);

        Assert.Equal(95.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A claim by somebody who has left cannot become a stored share: their money would sit
    /// in the ledger belonging to nobody the balance listing shows, and the group's column
    /// would stop summing to zero.
    /// </summary>
    /// <remarks>
    /// The itemised handler deliberately does not filter its claims the way a weighted rule
    /// filters its participants -- there is no honest way to redistribute a steak somebody
    /// ordered -- so the refusal has to come from the splitter.
    /// </remarks>
    [Fact]
    public async Task A_bill_claimed_by_somebody_who_has_left_is_refused()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 50.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 50.00m }]
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 30.00m, [Self]),
            ("Pasta", 20.00m, [other]));

        await Departs(groupId, other);

        var thrown = await Assert.ThrowsAsync<ConflictException>(
            () => GetService<IReceiptService>().Divide(expense.Id, Ct));

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, thrown.Code);
    }

    /// <summary>
    /// Reading an expense you paid for outlives leaving its group; changing what everybody
    /// there owes does not.
    /// </summary>
    [Fact]
    public async Task Somebody_who_has_left_the_group_cannot_divide_its_expenses_by_a_bill()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 50.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits =
            [
                new SplitInput { UserId = Self, Amount = 25.00m },
                new SplitInput { UserId = other, Amount = 25.00m }
            ]
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m, ("Shared plate", 50.00m, [Self, other]));

        // The payer leaves. They can still see the expense -- it is theirs -- and must not go
        // on moving the balances of a group they are not in.
        await Departs(groupId, Self);

        var receipts = GetService<IReceiptService>();

        var thrown = await Assert.ThrowsAsync<ConflictException>(
            () => receipts.Divide(expense.Id, Ct));

        Assert.Equal(ErrorCodes.TransactionGroupLeft, thrown.Code);

        await Assert.ThrowsAsync<ConflictException>(() => receipts.DeleteForExpense(expense.Id, Ct));
    }

    /// <summary>
    /// Dividing replaces the shares rather than adding to them.
    /// </summary>
    /// <remarks>
    /// ExpenseSplitter works out what to delete from the expense's own Splits collection, so
    /// an expense loaded without them looks like one that has none and the old rows are left
    /// behind. Asserted on the stored rows rather than the returned entity, because the
    /// tracked graph is exactly what would hide it.
    /// </remarks>
    [Fact]
    public async Task Dividing_replaces_the_shares_it_already_had()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 60.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 60.00m }]
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 40.00m, [Self]),
            ("Pasta", 20.00m, [other]));

        await GetService<IReceiptService>().Divide(expense.Id, Ct);

        var splits = await SplitsOf(expense.Id);

        Assert.Equal(2, splits.Count);
        Assert.Equal(60.00m, splits.Sum(split => split.Amount));
        Assert.Equal(40.00m, splits.Single(split => split.UserId == Self).Amount);
    }

    /// <summary>
    /// A rule edited from one kind to itemised does not retroactively change how an expense
    /// already recorded is divided -- and dividing by the bill must not quietly store the
    /// older division while reporting the newer one.
    /// </summary>
    [Fact]
    public async Task An_expense_written_under_an_older_kind_is_not_divided_as_though_it_were_itemised()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var rule = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Dinners",
            Definition = new EvenSplitRuleDto()
        }, Ct);

        var category = await Categories.Create(new CreateCategoryRequest
        {
            GroupId = group.Id,
            Name = "Dinners",
            DefaultSplitRuleId = rule.Id
        }, Ct);

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 100.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            CategoryId = category.Id
        }, Ct);

        // The rule becomes itemised *after* the expense was written, so the expense still
        // points at the even version and is still an even split.
        await Rules.Update(rule.Id, new UpdateSplitRuleRequest
        {
            Name = "Dinners",
            Definition = new ItemizedSplitRuleDto()
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 80.00m, [Self]),
            ("Pasta", 20.00m, [other.Id]));

        var division = await GetService<IReceiptService>().Preview(expense.Id, Ct);
        await GetService<IReceiptService>().Divide(expense.Id, Ct);

        var splits = await SplitsOf(expense.Id);

        // Whatever it stores, what it reports must be what it stored. The bug this guards was
        // storing 50/50 from the old even version while answering 80/20 from the receipt.
        Assert.Equal(
            division.Shares.Single(share => share.UserId == Self).Amount,
            splits.Single(split => split.UserId == Self).Amount);

        Assert.Equal(100.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// Moving the amount of an expense that is split by its bill leaves the bill describing
    /// different money, and the refusal has to say so -- not blame shares nobody typed.
    /// </summary>
    [Fact]
    public async Task Editing_the_amount_under_an_itemized_rule_names_the_bill()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 120.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 120.00m }]
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 80.00m, [Self]),
            ("Pasta", 40.00m, [other]));

        await GetService<IReceiptService>().Divide(expense.Id, Ct);

        var thrown = await Assert.ThrowsAsync<UnprocessableException>(
            () => Transactions.Update(expense.Id, new UpdateTransactionRequest
            {
                Name = "Dinner",
                Amount = 125.00m,
                DateTime = expense.DateTime,
                PaidByUserId = Self,
                GroupId = groupId,
                CategoryId = categoryId
            }, Ct).AsTask());

        // The bill, by name and with both figures -- not "the shares add up to 120.00".
        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, thrown.Code);
        Assert.Contains("bill", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The preview of an edit has to reach the same answer as the save of it. It builds a
    /// detached draft, and a draft that does not carry the expense's own id cannot find the
    /// bill the division reads.
    /// </summary>
    [Fact]
    public async Task Previewing_an_edit_finds_the_bill_that_the_save_would()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 100.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 100.00m }]
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 70.00m, [Self]),
            ("Pasta", 30.00m, [other]));

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = "Dinner",
            Amount = 100.00m,
            DateTime = expense.DateTime,
            PaidByUserId = Self,
            GroupId = groupId,
            CategoryId = categoryId
        }, redivide: true, Ct);

        Assert.Equal(70.00m, preview.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(30.00m, preview.Splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// A bill on an expense nobody shares cannot be divided at all, so it is refused when it
    /// is attached rather than when the button is pressed.
    /// </summary>
    [Fact]
    public async Task A_bill_cannot_be_attached_to_a_personal_expense()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Groceries",
            Amount = 30.00m,
            DateTime = DateTimeOffset.UtcNow
        }, Ct);

        var thrown = await Assert.ThrowsAsync<ValidationException>(
            () => GetService<IReceiptService>().SaveForExpense(expense.Id, new SaveReceiptRequest
            {
                Subtotal = 30.00m,
                Total = 30.00m,
                Items = [new ReceiptItemInput { Name = "Bread", TotalPrice = 30.00m }]
            }, Ct));

        Assert.Equal(ErrorCodes.SplitOnAPersonalExpense, thrown.Code);
    }

    /// <summary>
    /// An expense filed under an itemised rule with no bill at all is refused by name rather
    /// than falling back to an even split -- which is the thing somebody itemising a dinner
    /// was trying to avoid.
    /// </summary>
    /// <remarks>
    /// Through <c>Create</c>, which is where the handler actually runs. Asking
    /// <c>ReceiptService.Divide</c> instead proved nothing: it looks the bill up first and
    /// answers RECEIPT_NOT_FOUND from there, so the assertion passed with the handler's whole
    /// refusal deleted.
    /// </remarks>
    [Fact]
    public async Task An_expense_under_an_itemized_rule_with_no_bill_is_refused_by_name()
    {
        var (groupId, _, categoryId) = await ItemizedGroup();

        var thrown = await Assert.ThrowsAsync<UnprocessableException>(
            () => Transactions.Create(new CreateTransactionRequest
            {
                Name = "Dinner",
                Amount = 40.00m,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = groupId,
                CategoryId = categoryId
                // No Splits: the category's rule divides it, and its rule is the bill.
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.ReceiptNotFound, thrown.Code);

        // And nothing was divided evenly behind the refusal.
        Assert.Empty(await DbContext.Set<Expense>().Where(e => e.GroupId == groupId).ToListAsync(Ct));
    }

    /// <summary>
    /// The last of the four ways the response used to promise a division that would then be
    /// refused -- and the only one that arises from an ordinary group action rather than an
    /// edit to the expense.
    /// </summary>
    [Fact]
    public async Task A_bill_claimed_by_somebody_who_has_left_reports_that_it_cannot_divide()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 50.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 50.00m }]
        }, Ct);

        await Bill(expense.Id, tax: 0m, tip: 0m,
            ("Steak", 30.00m, [Self]),
            ("Pasta", 20.00m, [other]));

        var receipts = GetService<IReceiptService>();

        // Every figure still agrees; what changed is who may be given a share.
        Assert.True((await receipts.ResponseFor(await receipts.ForExpense(expense.Id, Ct), expense.Id, Ct))
            .CanDivide);

        await Departs(groupId, other);

        Assert.False((await receipts.ResponseFor(await receipts.ForExpense(expense.Id, Ct), expense.Id, Ct))
            .CanDivide);
    }

    /// <summary>
    /// The fifth and last way the flag could promise a division that would be refused: not
    /// the bill and not the claimants, but the person reading it.
    /// </summary>
    /// <remarks>
    /// Reading reaches further than writing -- the payer of a group expense goes on seeing it
    /// after they leave -- so a caller who claims nothing on the bill passes every other
    /// check here and is refused by <c>MineToChange</c>.
    /// </remarks>
    [Fact]
    public async Task A_caller_who_has_left_is_told_the_bill_cannot_be_divided()
    {
        var (groupId, other, categoryId) = await ItemizedGroup();

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 40.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId,
            Splits = [new SplitInput { UserId = Self, Amount = 40.00m }]
        }, Ct);

        // Claimed entirely by the other member, so leaving does not make the payer a
        // departed *claimant* -- which is the case the previous test covers.
        await Bill(expense.Id, tax: 0m, tip: 0m, ("Everything", 40.00m, [other]));

        var receipts = GetService<IReceiptService>();

        Assert.True((await receipts.ResponseFor(await receipts.ForExpense(expense.Id, Ct), expense.Id, Ct))
            .CanDivide);

        await Departs(groupId, Self);

        Assert.False((await receipts.ResponseFor(await receipts.ForExpense(expense.Id, Ct), expense.Id, Ct))
            .CanDivide);
    }

    /// <summary>
    /// A bill that has stopped being its expense's money cannot be divided, and the response
    /// says so before anybody presses the button.
    /// </summary>
    /// <remarks>
    /// The drift is not a receipt operation: the bill is pinned to the amount when it is
    /// saved, and then an ordinary edit moves the amount out from under it.
    /// </remarks>
    [Fact]
    public async Task A_bill_that_no_longer_matches_its_expense_reports_that_it_cannot_divide()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 100.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id
        }, Ct);

        var receipts = GetService<IReceiptService>();

        await receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 100.00m,
            Total = 100.00m,
            Items =
            [
                new ReceiptItemInput
                {
                    Name = "Everything",
                    TotalPrice = 100.00m,
                    Split = ReceiptItemSplit.Evenly
                }
            ]
        }, Ct);

        // No itemised rule here, so nothing stops the amount moving.
        await Transactions.Update(expense.Id, new UpdateTransactionRequest
        {
            Name = "Dinner",
            Amount = 200.00m,
            DateTime = expense.DateTime,
            PaidByUserId = Self,
            GroupId = group.Id
        }, Ct);

        var bill = await receipts.ForExpense(expense.Id, Ct);

        // Through the service, which is what the endpoint does: the answer depends on who may
        // be given a share, and only the service can ask.
        Assert.False((await receipts.ResponseFor(bill, expense.Id, Ct)).CanDivide);

        var thrown = await Assert.ThrowsAsync<UnprocessableException>(
            () => receipts.Divide(expense.Id, Ct));

        // Names the bill, not shares nobody typed.
        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, thrown.Code);
        Assert.Contains("bill", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }
}

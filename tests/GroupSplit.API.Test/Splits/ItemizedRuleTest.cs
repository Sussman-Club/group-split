using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

public class ItemizedRuleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IReceiptService Receipts => GetService<IReceiptService>();
    private ISplitRuleService Rules => GetService<ISplitRuleService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private Guid Self => GetService<ICurrentUser>().User.Id;

    private async Task<(Expense Expense, Guid Other, Guid Even, Guid Sole)> Setup(decimal amount = 48m)
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Dinner" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);
        // The one the group was given, not one made here: a second is refused now.
        var itemized = await BillRuleOf(group.Id);
        var category = await GetService<ICategoryService>().Create(new CreateCategoryRequest
            { GroupId = group.Id, Name = "Food", DefaultSplitRuleId = itemized.SplitRuleId }, Ct);
        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner", Amount = amount, DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id, CategoryId = category.Id,
            Splits = [new SplitInput { UserId = Self, Amount = amount }]
        }, Ct);
        var even = await Rule(group.Id, "Together", new EvenSplitRuleDto());
        var sole = await Rule(group.Id, "Other only", new SharesSplitRuleDto { Shares = new Dictionary<Guid, int> { [other.Id] = 1 } });
        return (expense, other.Id, even.Id, sole.Id);
    }
    /// <summary>
    /// The open version of the "divide it by the bill" rule every group is given.
    /// </summary>
    private async Task<SplitRuleVersion> BillRuleOf(Guid group) =>
        await DbContext.Set<SplitRuleVersion>()
            .Include(version => version.SplitRule)
            .FirstAsync(version => version.SplitRule.Group.Id == group
                && version.SupersededAt == null
                && version is ItemizedSplitRuleVersion, Ct);

    private async Task<SplitRuleVersion> Rule(Guid group, string name, SplitRuleDto definition) =>
        (await Rules.Create(new CreateSplitRuleRequest { GroupId = group, Name = name, Definition = definition }, Ct)).Current!;
    private static ReceiptItemInput Item(string name, decimal price, Guid? rule, decimal tax = 0) =>
        new() { Name = name, UnitPrice = price, TotalPrice = price, TaxAmount = tax, SplitRuleVersionId = rule };
    private static SaveReceiptRequest Bill(params ReceiptItemInput[] items) => new()
    { Items = items, Subtotal = items.Sum(i => i.TotalPrice), Tax = items.Sum(i => i.TaxAmount),
        Total = items.Sum(i => i.TotalPrice + i.TaxAmount) };

    [Fact]
    public async Task Departed_sole_recipient_keeps_bill_readable_but_cannot_be_divided()
    {
        var (expense, other, even, _) = await Setup();
        var sole = await Rule(expense.GroupId, "Other", new SoleSplitRuleDto(other));
        var receipt = await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, sole.Id)), Ct);
        var membership = await DbContext.Set<GroupMembership>().SingleAsync(
            m => m.GroupId == expense.GroupId && m.UserId == other, Ct);
        DbContext.Remove(membership);
        await DbContext.SaveChangesAsync(Ct);
        DbContext.ChangeTracker.Clear();

        receipt = await Receipts.ForExpense(expense.Id, Ct);
        var response = await Receipts.ResponseFor(receipt, ct: Ct);
        Assert.True(response.CanEdit);
        Assert.False(response.CanDivide);
        var error = await Assert.ThrowsAsync<ConflictException>(() => Receipts.Preview(expense.Id, Ct));
        Assert.Equal(ErrorCodes.SplitUserNotInGroup, error.Code);
        await Assert.ThrowsAsync<ConflictException>(() => Receipts.Divide(expense.Id, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => Receipts.SetRule(expense.Id,
            receipt.Items.Single().Id, new SetReceiptItemRuleRequest { SplitRuleVersionId = sole.Id }, Ct));
        await Receipts.SetRule(expense.Id, receipt.Items.Single().Id,
            new SetReceiptItemRuleRequest { SplitRuleVersionId = even }, Ct);
        Assert.True((await Receipts.ResponseFor(receipt, ct: Ct)).CanDivide);
    }

    [Fact]
    public async Task Mixed_item_rules_aggregate_to_one_expense_and_preserve_parent_rule()
    {
        var (expense, other, even, sole) = await Setup();
        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Pizza", 24, even), Item("Wine", 18, sole), Item("Delivery", 6, even)), Ct);
        Assert.Equal(Self, Assert.Single(expense.Splits).UserId);
        var preview = await Receipts.Preview(expense.Id, Ct);
        Assert.Equal(15m, preview.Shares.Single(s => s.UserId == Self).Amount);
        Assert.Equal(33m, preview.Shares.Single(s => s.UserId == other).Amount);
        await Receipts.Divide(expense.Id, Ct);
        Assert.Equal(48m, expense.Splits.Sum(s => s.Amount));
        Assert.IsType<ItemizedSplitRuleVersion>(expense.SplitRuleVersion);
        Assert.Single(await DbContext.Set<Expense>().Where(e => e.GroupId == expense.GroupId).ToListAsync(Ct));
        Assert.Equal(expense.Id, bill.ExpenseId);
    }

    [Fact]
    public async Task Editing_a_saved_rule_does_not_change_an_existing_item()
    {
        var (expense, other, even, sole) = await Setup();
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, even)), Ct);
        var version = await DbContext.Set<SplitRuleVersion>().FindAsync([even], Ct);
        // Restated, not re-shaped: a rule keeps the kind it was written with, so the edit
        // that matters here is the one the group can actually make -- the same even split,
        // now naming one person instead of the whole group.
        await Rules.Update(version!.SplitRuleId, new UpdateSplitRuleRequest
            { Name = "Together", Definition = new EvenSplitRuleDto([Self]) }, Ct);
        DbContext.ChangeTracker.Clear();
        var preview = await Receipts.Preview(expense.Id, Ct);
        Assert.Equal(24m, preview.Shares.Single(s => s.UserId == other).Amount);
        Assert.Equal(even, Assert.Single((await Receipts.ForExpense(expense.Id, Ct)).Items).SplitRuleVersionId);
    }

    [Fact]
    public async Task Item_rules_can_change_or_be_cleared_without_changing_stored_shares()
    {
        var (expense, other, even, sole) = await Setup();
        var receipt = await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, even)), Ct);
        var id = Assert.Single(receipt.Items).Id;
        await Receipts.SetRule(expense.Id, id, new SetReceiptItemRuleRequest { SplitRuleVersionId = sole }, Ct);
        Assert.Equal(48m, (await Receipts.Preview(expense.Id, Ct)).Shares.Single(s => s.UserId == other).Amount);
        Assert.Equal(Self, Assert.Single(expense.Splits).UserId);
        await Receipts.SetRule(expense.Id, id, new SetReceiptItemRuleRequest(), Ct);
        Assert.False((await Receipts.ResponseFor(receipt, ct: Ct)).CanDivide);
        var error = await Assert.ThrowsAsync<UnprocessableException>(() => Receipts.Divide(expense.Id, Ct));
        Assert.Equal(ErrorCodes.ReceiptItemsMissingRule, error.Code);
    }

    [Fact]
    public async Task Rules_referenced_only_by_items_cannot_be_deleted()
    {
        var (expense, _, even, _) = await Setup();
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, even)), Ct);
        var version = await DbContext.Set<SplitRuleVersion>().FindAsync([even], Ct);
        var error = await Assert.ThrowsAsync<ConflictException>(() => Rules.Delete(version!.SplitRuleId, Ct));
        Assert.Equal(ErrorCodes.SplitRuleInUse, error.Code);
    }

    [Fact]
    public async Task Nested_itemized_rules_are_rejected_without_mutating_the_bill()
    {
        var (expense, _, even, _) = await Setup();
        var receipt = await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, even)), Ct);
        // The group's own bill rule, which is the only itemized one there is now -- and the
        // only way this nesting can still be attempted.
        var nested = await BillRuleOf(expense.GroupId);
        await Assert.ThrowsAsync<ValidationException>(() => Receipts.SetRule(expense.Id, receipt.Items.Single().Id,
            new SetReceiptItemRuleRequest { SplitRuleVersionId = nested.Id }, Ct));
        Assert.Equal(even, receipt.Items.Single().SplitRuleVersionId);
    }

    [Fact]
    public async Task A_rule_from_another_group_is_rejected()
    {
        var (expense, _, even, _) = await Setup();
        var (foreign, _, foreignRule, _) = await Setup();
        await Assert.ThrowsAsync<ValidationException>(() => Receipts.SaveForExpense(expense.Id,
            Bill(Item("Dinner", 48, foreignRule)), Ct));
        Assert.False(await DbContext.Set<Receipt>().AnyAsync(r => r.ExpenseId == expense.Id, Ct));
    }

    [Fact]
    public async Task Per_line_tax_and_proportional_tip_follow_the_item_rules()
    {
        var (expense, other, even, sole) = await Setup(239m);
        var mine = await Rule(expense.GroupId, "Mine", new SoleSplitRuleDto(Self));
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Food", 100, mine.Id, 6), Item("Goods", 100, sole, 23))
            with { Tip = 10, Total = 239 }, Ct);
        var shares = (await Receipts.Preview(expense.Id, Ct)).Shares;
        Assert.Equal(111m, shares.Single(s => s.UserId == Self).Amount);
        Assert.Equal(128m, shares.Single(s => s.UserId == other).Amount);
    }

    [Fact]
    public async Task Rounding_is_stable_after_reload_and_sums_to_the_expense()
    {
        var (expense, _, even, sole) = await Setup(0.07m);
        await Receipts.SaveForExpense(expense.Id, Bill(Item("A", .03m, even), Item("B", .03m, sole))
            with { Tip = .01m, Total = .07m }, Ct);
        var before = await Receipts.Preview(expense.Id, Ct);
        DbContext.ChangeTracker.Clear();
        var after = await Receipts.Preview(expense.Id, Ct);
        Assert.Equal(before.Shares, after.Shares);
        Assert.Equal(.07m, after.Shares.Sum(s => s.Amount));
    }

    [Fact]
    public async Task Invalid_totals_leave_the_saved_receipt_untouched()
    {
        var (expense, _, even, _) = await Setup();
        var receipt = await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, even)), Ct);
        await Assert.ThrowsAsync<UnprocessableException>(() => Receipts.SaveForExpense(expense.Id,
            Bill(Item("Wrong", 45, even)) with { Total = 48 }, Ct));
        Assert.Equal("Dinner", receipt.Items.Single().Name);
    }

    /// <summary>
    /// Previewing an edit that divides by the bill answers with the bill's own figures.
    /// </summary>
    /// <remarks>
    /// The draft the preview divides is built field by field rather than loaded, and it was
    /// built without the receipt -- so the itemized rule, which reads the bill off the
    /// context, refused every one of these with "this expense has no itemised bill on it"
    /// while the screen asking was displaying that very bill. It only bites when the division
    /// is actually recomputed, which is what choosing "By its bill" on the edit screen does,
    /// so nothing caught it until the option existed.
    /// </remarks>
    [Fact]
    public async Task Previewing_an_edit_divided_by_its_bill_reads_the_bill()
    {
        var (expense, other, even, _) = await Setup();
        var mine = await Rule(expense.GroupId, "Mine", new SoleSplitRuleDto(Self));
        var theirs = await Rule(expense.GroupId, "Theirs", new SoleSplitRuleDto(other));

        await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Mine", 30, mine.Id), Item("Theirs", 18, theirs.Id)), Ct);

        DbContext.ChangeTracker.Clear();

        var preview = await Transactions.PreviewUpdate(expense.Id, new UpdateTransactionRequest
        {
            Name = expense.Name,
            Amount = 48m,
            DateTime = expense.DateTime,
            GroupId = expense.GroupId,
            CategoryId = expense.CategoryId,
            PaidByUserId = Self
        }, redivide: true, Ct);

        // The bill's own division, line by line, and not an even split of the total.
        Assert.Equal(30m, preview.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(18m, preview.Splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// Explicitly choosing the bill must beat a category whose own rule divides evenly.
    /// </summary>
    [Fact]
    public async Task Choosing_the_bill_for_an_edit_uses_it_instead_of_the_category_rule()
    {
        var (expense, other, even, _) = await Setup();
        var evenRuleId = await DbContext.Set<SplitRuleVersion>()
            .Where(version => version.Id == even).Select(version => version.SplitRuleId)
            .SingleAsync(Ct);
        var category = await GetService<ICategoryService>().Create(new CreateCategoryRequest
        {
            GroupId = expense.GroupId,
            Name = "Even food",
            DefaultSplitRuleId = evenRuleId
        }, Ct);
        var mine = await Rule(expense.GroupId, "Mine", new SoleSplitRuleDto(Self));
        var theirs = await Rule(expense.GroupId, "Theirs", new SoleSplitRuleDto(other));

        await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Mine", 30, mine.Id), Item("Theirs", 18, theirs.Id)), Ct);

        var billRule = await BillRuleOf(expense.GroupId);
        var request = new UpdateTransactionRequest
        {
            Name = expense.Name,
            Amount = 48m,
            DateTime = expense.DateTime,
            GroupId = expense.GroupId,
            CategoryId = category.Id,
            PaidByUserId = Self,
            SplitRuleId = billRule.SplitRule.Id
        };

        var preview = await Transactions.PreviewUpdate(expense.Id, request, redivide: true, Ct);

        Assert.Equal(30m, preview.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(18m, preview.Splits.Single(split => split.UserId == other).Amount);
        Assert.Equal(billRule.SplitRule.Name, preview.RuleName);

        await Transactions.Update(expense.Id, request, Ct);

        var stored = await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id).ToListAsync(Ct);
        Assert.Equal(30m, stored.Single(split => split.UserId == Self).Amount);
        Assert.Equal(18m, stored.Single(split => split.UserId == other).Amount);
        Assert.Equal(billRule.Id,
            await DbContext.Set<Expense>().Where(candidate => candidate.Id == expense.Id)
                .Select(candidate => candidate.SplitRuleVersionId).FirstAsync(Ct));
    }

    /// <summary>
    /// Every group is given one, so nothing has to be created before a bill can divide one.
    /// </summary>
    [Fact]
    public async Task A_new_group_is_given_the_rule_that_divides_by_the_bill()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var held = await DbContext.Set<SplitRule>()
            .Where(rule => rule.Group.Id == group.Id)
            .Where(rule => rule.Versions.Any(v => v.SupersededAt == null && v is ItemizedSplitRuleVersion))
            .ToListAsync(Ct);

        var bill = Assert.Single(held);
        Assert.True(bill.BuiltIn);
    }

    /// <summary>
    /// And a second is refused, naming the one the group already holds. Two of them divide
    /// identically -- the rule has no settings -- so a second is a second name, and the names
    /// are what made clients pick the wrong one.
    /// </summary>
    [Fact]
    public async Task A_second_rule_that_divides_by_the_bill_is_refused()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id, Name = "By items too", Definition = new ItemizedSplitRuleDto()
        }, Ct));

        Assert.Equal(ErrorCodes.SplitRuleBillIsProvisioned, refused.Code);
    }

    /// <summary>
    /// The one it was given is not the group's to rename or delete, like the per-member rules
    /// -- and the refusal says which of the two kinds it is rather than calling it a member's.
    /// </summary>
    [Fact]
    public async Task The_bill_rule_cannot_be_renamed_or_deleted()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var bill = await DbContext.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .FirstAsync(rule => rule.Group.Id == group.Id
                && rule.Versions.Any(v => v.SupersededAt == null && v is ItemizedSplitRuleVersion), Ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Rules.Delete(bill.Id, Ct));

        Assert.Equal(ErrorCodes.SplitRuleNotEditable, refused.Code);
        Assert.Contains("dividing an expense by its own bill", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expense divided by its bill, 30 of it the payer's and 18 the other member's.
    /// </summary>
    private async Task<(Expense Expense, Guid Other)> DividedByItsBill()
    {
        var (expense, other, _, _) = await Setup();
        var mine = await Rule(expense.GroupId, "Mine", new SoleSplitRuleDto(Self));
        var theirs = await Rule(expense.GroupId, "Theirs", new SoleSplitRuleDto(other));
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Mine", 30, mine.Id), Item("Theirs", 18, theirs.Id)), Ct);
        await Receipts.Divide(expense.Id, Ct);
        return (expense, other);
    }

    private async Task<Dictionary<Guid, decimal>> SharesOf(Guid expense) =>
        await DbContext.Set<TransactionSplit>().AsNoTracking()
            .Where(split => split.TransactionId == expense)
            .ToDictionaryAsync(split => split.UserId, split => split.Amount, Ct);

    private Task<Receipt> StoredBill(Guid expense) =>
        DbContext.Set<Receipt>().AsNoTracking().SingleAsync(receipt => receipt.ExpenseId == expense, Ct);

    /// <summary>
    /// Somebody forgot the tip. The amount could not move because the bill no longer came to
    /// it, and the bill could not move because it had to come to the amount; now the amount
    /// moves and the bill follows, the difference on its tip, spread over the lines by price.
    /// </summary>
    [Fact]
    public async Task Changing_the_amount_puts_the_difference_on_the_tip_and_divides_again()
    {
        var (expense, other) = await DividedByItsBill();
        var edit = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        edit.Amount = 58m;

        // The preview says what the save will do, and changes nothing on the way.
        var preview = await Transactions.PreviewUpdate(expense.Id, edit, ct: Ct);
        Assert.Equal(36.25m, preview.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(21.75m, preview.Splits.Single(split => split.UserId == other).Amount);
        Assert.Equal(0m, (await StoredBill(expense.Id)).Tip);
        Assert.Equal(0m, (await Receipts.ForExpense(expense.Id, Ct)).Tip);

        await Transactions.Update(expense.Id, edit, Ct);

        var bill = await StoredBill(expense.Id);
        Assert.Equal(10m, bill.Tip);
        Assert.Equal(58m, bill.Total);
        var shares = await SharesOf(expense.Id);
        Assert.Equal(36.25m, shares[Self]);
        Assert.Equal(21.75m, shares[other]);
        Assert.True((await Receipts.ResponseFor(await Receipts.ForExpense(expense.Id, Ct), ct: Ct)).CanDivide);
    }

    /// <summary>
    /// A tip below nothing is not one any bill has, so taking off more than the tip is
    /// refused by name -- and refused before anything moved.
    /// </summary>
    [Fact]
    public async Task Lowering_the_amount_by_more_than_the_tip_is_refused_and_moves_nothing()
    {
        var (expense, other) = await DividedByItsBill();
        var edit = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        edit.Amount = 40m;

        var previewed = await Assert.ThrowsAsync<UnprocessableException>(
            () => Transactions.PreviewUpdate(expense.Id, edit, ct: Ct));
        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, previewed.Code);

        var refused = await Assert.ThrowsAsync<UnprocessableException>(
            () => Transactions.Update(expense.Id, edit, Ct).AsTask());
        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refused.Code);

        var bill = await StoredBill(expense.Id);
        Assert.Equal(48m, bill.Total);
        Assert.Equal(0m, bill.Tip);
        Assert.Equal(48m, await DbContext.Set<Expense>().AsNoTracking().Where(e => e.Id == expense.Id)
            .Select(e => e.Amount).SingleAsync(Ct));
        Assert.Equal(18m, (await SharesOf(expense.Id))[other]);
    }

    /// <summary>
    /// Shares somebody stated are not the bill's to restate: the amount alone is still
    /// refused on them, exactly as it is on any expense split by hand, and the bill is left
    /// as it was.
    /// </summary>
    [Fact]
    public async Task An_expense_split_by_hand_keeps_refusing_the_amount_alone_and_its_bill_stays()
    {
        var (expense, other, _, _) = await Setup();
        var theirs = await Rule(expense.GroupId, "Theirs", new SoleSplitRuleDto(other));
        await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48, theirs.Id)), Ct);

        var edit = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        edit.Amount = 58m;

        var refused = await Assert.ThrowsAsync<UnprocessableException>(
            () => Transactions.Update(expense.Id, edit, Ct).AsTask());
        Assert.Equal(ErrorCodes.SplitsDoNotSumToAmount, refused.Code);

        var bill = await StoredBill(expense.Id);
        Assert.Equal(48m, bill.Total);
        Assert.Equal(0m, bill.Tip);
        Assert.Equal(48m, (await SharesOf(expense.Id))[Self]);
    }

    /// <summary>
    /// A line's rule is the group's rule, naming the group's people. Moving the expense takes
    /// them off its bill, so the new group never reads the old one's rules.
    /// </summary>
    [Fact]
    public async Task Moving_the_expense_to_another_group_clears_its_bills_line_rules()
    {
        var (expense, _) = await DividedByItsBill();
        var elsewhere = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);
        var edit = (await Transactions.GetUpdateModel(expense.Id, Ct))!;
        edit.GroupId = elsewhere.Id;
        edit.CategoryId = null;
        edit.Splits = null;

        var preview = await Transactions.PreviewUpdate(expense.Id, edit, ct: Ct);
        Assert.Equal(48m, Assert.Single(preview.Splits).Amount);
        Assert.All(await DbContext.Set<ReceiptItem>().AsNoTracking().ToListAsync(Ct),
            item => Assert.NotNull(item.SplitRuleVersionId));

        await Transactions.Update(expense.Id, edit, Ct);

        Assert.All(await DbContext.Set<ReceiptItem>().AsNoTracking().ToListAsync(Ct),
            item => Assert.Null(item.SplitRuleVersionId));
        var response = await Receipts.ResponseFor(await Receipts.ForExpense(expense.Id, Ct), ct: Ct);
        Assert.Equal(2, response.MissingRuleItemCount);
        Assert.All(response.Items, item =>
        {
            Assert.Null(item.SplitRuleName);
            Assert.Null(item.SplitRule);
        });
    }

    /// <summary>
    /// A bill moved before moves cleared its rules still holds them. They are never shown:
    /// a line holding another group's rule reads as a line with no rule.
    /// </summary>
    [Fact]
    public async Task A_line_still_holding_another_groups_rule_is_shown_without_it()
    {
        var (expense, _) = await DividedByItsBill();
        var elsewhere = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);
        var stored = await DbContext.Set<Expense>().Include(e => e.Group).SingleAsync(e => e.Id == expense.Id, Ct);
        stored.Group = await DbContext.Set<GroupSplit.Data.Entities.Group>().SingleAsync(group => group.Id == elsewhere.Id, Ct);
        stored.GroupId = elsewhere.Id;
        stored.CategoryId = null;
        await DbContext.SaveChangesAsync(Ct);

        var bill = await Receipts.ForExpense(expense.Id, Ct);
        Assert.All(bill.Items, item => Assert.NotNull(item.SplitRuleVersionId));
        var response = await Receipts.ResponseFor(bill, ct: Ct);

        Assert.Equal(2, response.MissingRuleItemCount);
        Assert.All(response.Items, item =>
        {
            Assert.Null(item.SplitRuleVersionId);
            Assert.Null(item.SplitRuleName);
            Assert.Null(item.SplitRule);
        });
    }


    [Fact]
    public async Task Model_enforces_one_expense_per_bank_row_and_restricts_rule_version_deletion()
    {
        var transaction = DbContext.Model.FindEntityType(typeof(GroupSplit.Data.Entities.Transaction))!;
        // The uniqueness is on the relationship, not on an index: a one-to-one FK reads
        // the same under every provider, while the index behind it is the relational
        // store's business and is absent from the model the tests run against.
        Assert.True(transaction.GetForeignKeys().Single(f => f.Properties[0].Name == "BankTransactionId").IsUnique);
        var item = DbContext.Model.FindEntityType(typeof(ReceiptItem))!;
        Assert.Equal(DeleteBehavior.Restrict, item.GetForeignKeys().Single(f => f.Properties[0].Name == "SplitRuleVersionId").DeleteBehavior);
        Assert.Null(DbContext.Model.FindEntityType("GroupSplit.Data.Entities.ReceiptItemClaim"));
        await Task.CompletedTask;
    }
}

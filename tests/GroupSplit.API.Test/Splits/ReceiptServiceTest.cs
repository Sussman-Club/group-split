using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;

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

    [Fact]
    public async Task A_new_expense_previews_and_saves_its_itemized_receipt_in_one_operation()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Receipt dinner" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);
        var mine = await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group.Id, Name = "Mine", Definition = new SoleSplitRuleDto(Self) }, Ct);
        var theirs = await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group.Id, Name = "Theirs", Definition = new SoleSplitRuleDto(other.Id) }, Ct);
        var itemized = await DbContext.Set<ItemizedSplitRuleVersion>().Include(version => version.SplitRule)
            .SingleAsync(version => version.SplitRule.Group.Id == group.Id && version.SupersededAt == null, Ct);
        var request = new CreateTransactionRequest
        {
            GroupId = group.Id, Name = "Dinner from receipt", Amount = 32.25m, DateTime = DateTimeOffset.UtcNow,
            SplitRuleId = itemized.SplitRule.Id,
            Receipt = new SaveReceiptRequest
            {
                Subtotal = 30m, Tax = 2.25m, Total = 32.25m,
                Items = [Item("My meal", 10m, mine.Current!.Id) with { TaxRate = 7.5m },
                    Item("Their meal", 20m, theirs.Current!.Id) with { TaxRate = 7.5m }]
            }
        };
        var before = await DbContext.Set<Expense>().CountAsync(Ct);
        var preview = await Transactions.Preview(request, Ct);
        Assert.Equal(before, await DbContext.Set<Expense>().CountAsync(Ct));
        Assert.Equal(10.75m, preview.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(21.50m, preview.Splits.Single(split => split.UserId == other.Id).Amount);

        var saved = await Transactions.Create(request, Ct);
        var bill = await Receipts.ForExpense(saved.Id, Ct);
        Assert.Equal(2, bill.Items.Count);
        Assert.Equal(2.25m, bill.Items.Sum(item => item.TaxAmount));
        Assert.Equal(10.75m, saved.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(21.50m, saved.Splits.Single(split => split.UserId == other.Id).Amount);
        Assert.True((await Receipts.ResponseFor(bill, ct: Ct)).DividesItsExpense);
    }

    [Fact]
    public async Task An_invalid_creation_receipt_does_not_leave_an_expense_saved_with_another_split()
    {
        var before = await DbContext.Set<Expense>().CountAsync(Ct);
        await Assert.ThrowsAsync<UnprocessableException>(async () => await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Invalid receipt", Amount = 30m, DateTime = DateTimeOffset.UtcNow,
            Receipt = Bill(Item("Meal", 20m, null))
        }, Ct));
        Assert.Equal(before, await DbContext.Set<Expense>().CountAsync(Ct));
    }

    [Fact]
    public async Task A_complex_receipt_with_weighted_produce_multiple_rates_coupon_free_item_and_tip_saves_exactly()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Complex receipt" }, Ct);
        await JoinGroup(group.Id, await CreateNewUser());
        var even = await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group.Id, Name = "Together", Definition = new EvenSplitRuleDto() }, Ct);
        var itemized = await DbContext.Set<ItemizedSplitRuleVersion>().Include(version => version.SplitRule)
            .SingleAsync(version => version.SplitRule.Group.Id == group.Id && version.SupersededAt == null, Ct);
        ReceiptItemInput Taxed(string name, decimal price, decimal rate) =>
            Item(name, price, even.Current!.Id) with { TaxRate = rate, IsTaxable = true };
        var request = new CreateTransactionRequest
        {
            Name = "Mixed grocery receipt", GroupId = group.Id, Amount = 53.78m,
            DateTime = DateTimeOffset.UtcNow, SplitRuleId = itemized.SplitRule.Id,
            Receipt = new SaveReceiptRequest
            {
                Subtotal = 43.23m, Tax = 2.55m, Tip = 8m, Total = 53.78m,
                Items = [Taxed("Milk", 4m, 7.5m), Taxed("Bread", 3m, 7.5m), Taxed("Apples", 3m, 7.5m),
                    Item("Weighted produce", 8.24m, even.Current!.Id) with
                        { UnitPrice = 5.99m, Quantity = 1.375m, IsTaxable = false },
                    Taxed("Detergent", 19.99m, 7.5m), Taxed("Coupon", -2m, 7.5m),
                    Taxed("Nuts A", 1.05m, 10m), Taxed("Nuts B", 1.05m, 10m),
                    Item("Deposit", .10m, even.Current!.Id) with { IsTaxable = false },
                    Item("Free sample", 0m, even.Current!.Id) with { IsTaxable = false },
                    Taxed("Tea", 4.80m, 5m) with { UnitPrice = 1.60m, Quantity = 3m }]
            }
        };
        var preview = await Transactions.Preview(request, Ct);
        var saved = await Transactions.Create(request, Ct);
        var receipt = await Receipts.ForExpense(saved.Id, Ct);
        Assert.Equal(11, receipt.Items.Count);
        Assert.Equal(2.55m, receipt.Items.Sum(item => item.TaxAmount));
        Assert.Equal(-.15m, receipt.Items.Single(item => item.Name == "Coupon").TaxAmount);
        Assert.All(receipt.Items.Where(item => item.IsTaxable == false), item => Assert.Equal(0m, item.TaxAmount));
        Assert.Equal(53.78m, saved.Splits.Sum(split => split.Amount));
        Assert.Equal(preview.Splits.OrderBy(split => split.UserId).Select(split => (split.UserId, split.Amount)),
            saved.Splits.OrderBy(split => split.UserId).Select(split => (split.UserId, split.Amount)));
        Assert.True((await Receipts.ResponseFor(receipt, ct: Ct)).DividesItsExpense);
    }

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
    /// A personal expense is one person's money, so its itemized receipt is stored as a
    /// record but is never offered as a group division.
    /// </summary>
    [Fact]
    public async Task Marking_a_previously_taxed_line_exempt_clears_its_old_tax_before_allocation()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Receipt", Amount = 21m, DateTime = DateTimeOffset.UtcNow }, Ct);
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 20m, Tax = 1m, Total = 21m,
            Items = [
                new ReceiptItemInput { Name = "Now exempt", UnitPrice = 10m, TotalPrice = 10m, TaxAmount = .5m, IsTaxable = false },
                new ReceiptItemInput { Name = "Taxable", UnitPrice = 10m, TotalPrice = 10m, IsTaxable = true }]
        }, Ct);
        Assert.Equal(0m, bill.Items.Single(item => item.Name == "Now exempt").TaxAmount);
        Assert.Equal(1m, bill.Items.Single(item => item.Name == "Taxable").TaxAmount);
    }

    [Fact]
    public async Task A_personal_expense_keeps_an_itemized_receipt_without_dividing()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Lunch", Amount = 10m, DateTime = DateTimeOffset.UtcNow }, Ct);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Sandwich", 10m, null)), Ct);
        var response = await Receipts.ResponseFor(bill, ct: Ct);

        Assert.Equal("Sandwich", Assert.Single(bill.Items).Name);
        Assert.True(response.CanEdit);
        Assert.False(response.CanDivide);

        var updated = await Receipts.SaveForExpense(expense.Id, Bill(Item("Soup", 10m, null)), Ct);

        Assert.Equal("Soup", Assert.Single(updated.Items).Name);
    }

    [Fact]
    public async Task A_receipt_persists_source_metadata_and_signed_coupon_lines()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Shopping", Amount = 10m, DateTime = DateTimeOffset.UtcNow }, Ct);

        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            MerchantName = "Corner Store",
            PurchaseDate = new DateOnly(2026, 9, 23),
            Subtotal = 10m,
            Tax = 0m,
            Tip = 0m,
            Total = 10m,
            Items = [
                new ReceiptItemInput { Name = "Coffee", UnitPrice = 12m, Quantity = 1m,
                    TotalPrice = 12m, IsTaxable = false },
                new ReceiptItemInput { Name = "Loyalty coupon SAVE2", UnitPrice = -2m,
                    TotalPrice = -2m }]
        }, Ct);

        var response = await Receipts.ResponseFor(bill, ct: Ct);

        Assert.Equal("Corner Store", response.MerchantName);
        Assert.Equal(new DateOnly(2026, 9, 23), response.PurchaseDate);
        Assert.Equal(12m, response.Items[0].TotalPrice);
        Assert.Equal(-2m, response.Items[1].TotalPrice);

        var updated = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 10m, Total = 10m,
            Items = [
                new ReceiptItemInput { Id = response.Items[0].Id, Name = "Coffee",
                    UnitPrice = 12m, TotalPrice = 12m, IsTaxable = false },
                new ReceiptItemInput { Id = response.Items[1].Id, Name = "Loyalty coupon SAVE2",
                    UnitPrice = -2m, TotalPrice = -2m }]
        }, Ct);
    }

    [Fact]
    public async Task Two_small_coupons_remain_distinct_negative_lines()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Shopping", Amount = .01m, DateTime = DateTimeOffset.UtcNow }, Ct);
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = .01m, Total = .01m,
            Items = [Item("A", .01m, null), Item("B", .01m, null), Item("C", .01m, null),
                Item("Coupon A", -.01m, null), Item("Coupon B", -.01m, null)]
        }, Ct);

        Assert.Equal(.01m, bill.Total);
        Assert.Equal(.01m, bill.Items.Sum(item => item.TotalPrice));
        Assert.Equal(2, bill.Items.Count(item => item.TotalPrice < 0));
    }

    [Fact]
    public async Task A_coupon_uses_its_own_rule_and_a_free_item_stays_on_the_bill()
    {
        var (expense, group, even) = await AnExpense();
        var mine = (await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group, Name = "My coupon", Definition = new SoleSplitRuleDto(Self)
        }, Ct)).Current!.Id;

        var bill = await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Dinner", 49m, even), Item("Coupon", -2m, mine), Item("Free sample", 0m, even))
                with { Tip = 1m, Total = 48m }, Ct);
        var preview = await Receipts.Preview(expense.Id, Ct);

        Assert.Equal(3, bill.Items.Count);
        Assert.Equal(23m, preview.Shares.Single(share => share.UserId == Self).Amount);
        Assert.Equal(25m, preview.Shares.Single(share => share.UserId != Self).Amount);
    }

    [Theory]
    [InlineData(4, 8, .96, .32, .64)]
    [InlineData(1, 1, .01, .01, 0)]
    public async Task Total_only_tax_is_allocated_on_save_and_survives_round_trip(
        double first, double second, double tax, double firstTax, double secondTax)
    {
        var subtotal = (decimal)first + (decimal)second;
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Receipt", Amount = subtotal + (decimal)tax, DateTime = DateTimeOffset.UtcNow }, Ct);
        var request = new SaveReceiptRequest
        {
            Subtotal = subtotal, Tax = (decimal)tax, Total = subtotal + (decimal)tax,
            Items = [new ReceiptItemInput { Name = "First", UnitPrice = (decimal)first, TotalPrice = (decimal)first, IsTaxable = true },
                new ReceiptItemInput { Name = "Second", UnitPrice = (decimal)second, TotalPrice = (decimal)second, IsTaxable = true },
                Item("Free", 0m, null)]
        };
        var bill = await Receipts.SaveForExpense(expense.Id, request, Ct);
        Assert.Equal((decimal)firstTax, bill.Items.Single(item => item.Name == "First").TaxAmount);
        Assert.Equal((decimal)secondTax, bill.Items.Single(item => item.Name == "Second").TaxAmount);
        Assert.Equal(0m, bill.Items.Single(item => item.Name == "Free").TaxAmount);
        Assert.Equal((decimal)tax, bill.Items.Sum(item => item.TaxAmount));
        var savedAgain = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = subtotal, Tax = (decimal)tax, Total = subtotal + (decimal)tax,
            Items = bill.Items.Select(item => new ReceiptItemInput
            {
                Id = item.Id, Name = item.Name, UnitPrice = item.UnitPrice,
                Quantity = item.Quantity, TotalPrice = item.TotalPrice, TaxAmount = item.TaxAmount, IsTaxable = item.IsTaxable
            }).ToArray()
        }, Ct);
        Assert.Equal((decimal)tax, savedAgain.Items.Sum(item => item.TaxAmount));
    }

    [Fact]
    public async Task Extracted_tax_is_kept_and_only_missing_tax_on_identified_taxable_items_is_spread()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Receipt", Amount = 32m, DateTime = DateTimeOffset.UtcNow }, Ct);
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 30m, Tax = 2m, Total = 32m,
            Items = [
                new ReceiptItemInput { Name = "Printed", UnitPrice = 10m, TotalPrice = 10m,
                    TaxAmount = .80m, TaxRate = 10m, IsTaxable = true },
                new ReceiptItemInput { Name = "Taxable", UnitPrice = 10m, TotalPrice = 10m, IsTaxable = true },
                new ReceiptItemInput { Name = "Exempt", UnitPrice = 5m, TotalPrice = 5m, IsTaxable = false },
                new ReceiptItemInput { Name = "Unknown", UnitPrice = 5m, TotalPrice = 5m }]
        }, Ct);
        Assert.Equal(.80m, bill.Items.Single(item => item.Name == "Printed").TaxAmount);
        Assert.Equal(1.20m, bill.Items.Single(item => item.Name == "Taxable").TaxAmount);
        Assert.Equal(0m, bill.Items.Single(item => item.Name == "Exempt").TaxAmount);
        Assert.Equal(0m, bill.Items.Single(item => item.Name == "Unknown").TaxAmount);
    }

    [Fact]
    public async Task Tax_rates_apply_to_signed_lines_and_leave_exempt_items_untaxed()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Shopping", Amount = 16.50m, DateTime = DateTimeOffset.UtcNow }, Ct);
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 15m, Tax = 1.50m, Total = 16.50m,
            Items = [
                new ReceiptItemInput { Name = "Taxed", UnitPrice = 10m, TotalPrice = 10m,
                    TaxRate = 20m, IsTaxable = true },
                new ReceiptItemInput { Name = "Exempt", UnitPrice = 10m, TotalPrice = 10m,
                    TaxRate = 0m, IsTaxable = false },
                new ReceiptItemInput { Name = "Taxed coupon", UnitPrice = -5m, TotalPrice = -5m,
                    TaxRate = 10m, IsTaxable = true }]
        }, Ct);

        Assert.Equal(2m, bill.Items.Single(item => item.Name == "Taxed").TaxAmount);
        Assert.Equal(0m, bill.Items.Single(item => item.Name == "Exempt").TaxAmount);
        Assert.Equal(-.50m, bill.Items.Single(item => item.Name == "Taxed coupon").TaxAmount);
    }

    /// <summary>
    /// Two rates on one bill, typed in line by line. Marking one line taxable says nothing
    /// about the other, so neither figure moves.
    /// </summary>
    [Fact]
    public async Task A_tax_flag_on_one_line_keeps_the_other_lines_reviewed_tax()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Shopping", Amount = 21.80m, DateTime = DateTimeOffset.UtcNow }, Ct);
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 20m, Tax = 1.80m, Total = 21.80m,
            Items = [
                new ReceiptItemInput { Name = "Food", UnitPrice = 10m, TotalPrice = 10m,
                    TaxAmount = .80m, IsTaxable = true },
                new ReceiptItemInput { Name = "Wine", UnitPrice = 10m, TotalPrice = 10m,
                    TaxAmount = 1m }]
        }, Ct);

        Assert.Equal(.80m, bill.Items.Single(item => item.Name == "Food").TaxAmount);
        Assert.Equal(1m, bill.Items.Single(item => item.Name == "Wine").TaxAmount);
    }

    /// <summary>
    /// The till taxed the 2.10 once and printed 0.21; each line on its own rounds to 0.11.
    /// That cent is rounding, not a misread, and the bill still saves at the printed tax.
    /// </summary>
    [Fact]
    public async Task Per_line_rate_rounding_settles_on_the_printed_tax()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Shopping", Amount = 2.31m, DateTime = DateTimeOffset.UtcNow }, Ct);
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 2.10m, Tax = .21m, Total = 2.31m,
            Items = [
                new ReceiptItemInput { Name = "A", UnitPrice = 1.05m, TotalPrice = 1.05m, TaxRate = 10m },
                new ReceiptItemInput { Name = "B", UnitPrice = 1.05m, TotalPrice = 1.05m, TaxRate = 10m }]
        }, Ct);

        Assert.Equal(.21m, bill.Items.Sum(item => item.TaxAmount));
        Assert.Equal([.10m, .11m], bill.Items.Select(item => item.TaxAmount).Order());
    }

    [Fact]
    public async Task A_rated_bill_whose_tax_is_off_by_more_than_rounding_is_refused()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
            { Name = "Shopping", Amount = 11.50m, DateTime = DateTimeOffset.UtcNow }, Ct);

        var refusal = await Assert.ThrowsAsync<UnprocessableException>(() =>
            Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
            {
                Subtotal = 10m, Tax = 1.50m, Total = 11.50m,
                Items = [new ReceiptItemInput { Name = "A", UnitPrice = 10m, TotalPrice = 10m, TaxRate = 10m }]
            }, Ct));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refusal.Code);
    }

    /// <summary>
    /// A coupon put on somebody who bought nothing would credit them on an expense they did
    /// not pay. The bill is refused for division until the coupon goes with what it discounts.
    /// </summary>
    [Fact]
    public async Task A_coupon_on_somebody_who_bought_nothing_is_not_divided()
    {
        var (expense, group, _) = await AnExpense();
        var mine = (await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group, Name = "My coupon", Definition = new SoleSplitRuleDto(Self)
        }, Ct)).Current!.Id;
        var other = (await GetService<IGroupParticipants>().IdsOf(group, Ct)).Single(id => id != Self);
        var theirs = (await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group, Name = "Theirs", Definition = new SoleSplitRuleDto(other)
        }, Ct)).Current!.Id;

        await Receipts.SaveForExpense(expense.Id,
            Bill(Item("Dinner", 50m, theirs), Item("Coupon", -2m, mine)), Ct);

        var refusal = await Assert.ThrowsAsync<UnprocessableException>(() => Receipts.Preview(expense.Id, Ct));
        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refusal.Code);
    }

    [Fact]
    public async Task A_personal_receipt_cannot_use_a_group_split_rule()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Dinner" }, Ct);
        var even = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Together",
            Definition = new EvenSplitRuleDto()
        }, Ct);
        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Lunch",
            Amount = 10m,
            DateTime = DateTimeOffset.UtcNow
        }, Ct);

        var refusal = await Assert.ThrowsAsync<ValidationException>(() =>
            Receipts.SaveForExpense(expense.Id, Bill(Item("Sandwich", 10m, even.Current!.Id)), Ct));

        Assert.Equal(ErrorCodes.ReceiptInvalid, refusal.Code);
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
    /// The source file belongs to the expense even before its bill is transcribed. Saving
    /// the itemisation associates it with that bill, and deleting the bill preserves the
    /// source file while clearing only that association.
    /// </summary>
    [Fact]
    public async Task Source_files_are_linked_when_the_bill_is_transcribed_and_survive_its_removal()
    {
        var (expense, _, even) = await AnExpense();
        var attachment = new ReceiptAttachment
        {
            ExpenseId = expense.Id,
            ObjectKey = $"{expense.Id:N}/{Guid.NewGuid():N}",
            FileName = "dinner.jpg",
            ContentType = "image/jpeg",
            Length = 42,
            UploadedByUserId = Self,
            UploadedAt = DateTimeOffset.UtcNow
        };
        DbContext.Add(attachment);
        await DbContext.SaveChangesAsync(Ct);

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(Item("Dinner", 48m, even)), Ct);

        Assert.Equal(bill.Id, attachment.ReceiptId);
        Assert.Same(bill, attachment.Receipt);

        await Receipts.DeleteForExpense(expense.Id, Ct);

        var preserved = await DbContext.Set<ReceiptAttachment>()
            .SingleAsync(candidate => candidate.Id == attachment.Id, Ct);
        Assert.Equal(expense.Id, preserved.ExpenseId);
        Assert.Null(preserved.ReceiptId);
    }

    [Fact]
    public async Task Item_name_normalization_and_original_description_are_preserved()
    {
        var (expense, _, even) = await AnExpense();
        var item = new ReceiptItemInput
        {
            Name = "Pizza, large",
            NormalizedName = "Pizza large",
            Description = "PZA LG",
            UnitPrice = 48m,
            TotalPrice = 48m,
            SplitRuleVersionId = even
        };

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(item), Ct);
        var saved = Assert.Single(bill.Items);

        Assert.Equal("Pizza, large", saved.Name);
        Assert.Equal("pizza large", saved.NormalizedName);
        Assert.Equal("PZA LG", saved.Description);

        var response = await Receipts.ResponseFor(bill, ct: Ct);
        var responseItem = Assert.Single(response.Items);
        Assert.Equal("pizza large", responseItem.NormalizedName);
        Assert.Equal("PZA LG", responseItem.Description);
    }

    [Fact]
    public async Task Patching_one_item_preserves_omitted_fields_and_the_other_lines()
    {
        var (expense, _, even) = await AnExpense();
        var bill = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Subtotal = 48m,
            Total = 48m,
            Items =
            [
                new ReceiptItemInput
                {
                    Name = "Water",
                    Description = "KIRKLAND WATER 40 PK",
                    UnitPrice = 18m,
                    TotalPrice = 18m,
                    SplitRuleVersionId = even
                },
                new ReceiptItemInput
                {
                    Name = "Pizza",
                    Description = "LARGE CHEESE",
                    UnitPrice = 30m,
                    TotalPrice = 30m,
                    SplitRuleVersionId = even
                }
            ]
        }, Ct);
        var item = bill.Items.First(item => item.Name == "Water");
        var patch = new JsonPatchDocument<ReceiptItemPatch>();
        patch.Replace(line => line.Name, "Bottled water");

        var updated = await Receipts.PatchItem(expense.Id, item.Id, patch, Ct);

        var water = updated.Items.Single(line => line.Id == item.Id);
        var pizza = updated.Items.Single(line => line.Name == "Pizza");
        Assert.Equal("Bottled water", water.Name);
        Assert.Equal("KIRKLAND WATER 40 PK", water.Description);
        Assert.Equal(even, water.SplitRuleVersionId);
        Assert.Equal("LARGE CHEESE", pizza.Description);
    }

    /// <summary>
    /// Once an itemized receipt has produced the ledger shares, correcting its figures must
    /// update those shares in the same save. Otherwise the bill and balances disagree until
    /// somebody happens to press Divide again.
    /// </summary>
    [Fact]
    public async Task Editing_a_receipt_that_divided_the_expense_updates_stored_shares()
    {
        var (expense, group, even) = await AnExpense();
        var other = await DbContext.Set<GroupMembership>()
            .Where(membership => membership.GroupId == group && membership.UserId != Self)
            .Select(membership => membership.UserId)
            .SingleAsync(Ct);
        var sole = (await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group,
            Name = "Other only",
            Definition = new SoleSplitRuleDto(other)
        }, Ct)).Current!.Id;

        await Receipts.SaveForExpense(expense.Id, Bill(
            Item("Shared", 24m, even), Item("Other", 24m, sole)), Ct);
        await Receipts.Divide(expense.Id, Ct);

        await Receipts.SaveForExpense(expense.Id, Bill(
            Item("Shared", 30m, even), Item("Other", 18m, sole)), Ct);

        var details = await Transactions.GetDetails(expense.Id, Ct);
        Assert.Equal(15m, details!.Splits.Single(split => split.UserId == Self).Amount);
        Assert.Equal(33m, details.Splits.Single(split => split.UserId == other).Amount);
    }

    /// <summary>
    /// A bill that divided the expense, with a line split in two -- the new half starting on
    /// the default -- then made incomplete by clearing it, and completed again. The
    /// incomplete bill keeps the shares; the call that completes it -- the one <c>receipts rule</c> makes -- divides again. It used
    /// to find the bill "not the source" because it had been incomplete a moment before, and
    /// the ledger stayed where it was for good.
    /// </summary>
    [Fact]
    public async Task Completing_a_bill_that_divides_the_expense_divides_it_again()
    {
        var (expense, group, even) = await AnExpense();
        var other = await DbContext.Set<GroupMembership>()
            .Where(membership => membership.GroupId == group && membership.UserId != Self)
            .Select(membership => membership.UserId)
            .SingleAsync(Ct);
        var sole = (await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group, Name = "Other only", Definition = new SoleSplitRuleDto(other)
        }, Ct)).Current!.Id;

        var bill = await Receipts.SaveForExpense(expense.Id, Bill(
            Item("Shared", 24m, even), Item("Other", 24m, sole)), Ct);
        await Receipts.Divide(expense.Id, Ct);
        var shared = bill.Items.Single(item => item.Name == "Shared").Id;
        var theirs = bill.Items.Single(item => item.Name == "Other").Id;

        // Their 24 was two things, and only one of them was theirs alone. The new half starts
        // on the group's default -- Even -- so the bill stays complete and the ledger follows.
        bill = await Receipts.SaveForExpense(expense.Id, Bill(
            Item("Shared", 24m, even, shared), Item("Other", 12m, sole, theirs), Item("Dessert", 12m, null)), Ct);
        var dessert = bill.Items.Single(item => item.Name == "Dessert").Id;
        var split = (await Transactions.GetDetails(expense.Id, Ct))!.Splits;
        Assert.Equal(18m, split.Single(share => share.UserId == Self).Amount);
        Assert.Equal(30m, split.Single(share => share.UserId == other).Amount);

        // Cleared, the bill is incomplete and the ledger keeps the last shares it gave.
        await Receipts.SetRule(expense.Id, dessert, new SetReceiptItemRuleRequest(), Ct);
        split = (await Transactions.GetDetails(expense.Id, Ct))!.Splits;
        Assert.Equal(18m, split.Single(share => share.UserId == Self).Amount);

        // The dessert was theirs too.
        await Receipts.SetRule(expense.Id, dessert, new SetReceiptItemRuleRequest { SplitRuleVersionId = sole }, Ct);

        split = (await Transactions.GetDetails(expense.Id, Ct))!.Splits;
        Assert.Equal(12m, split.Single(share => share.UserId == Self).Amount);
        Assert.Equal(36m, split.Single(share => share.UserId == other).Amount);
    }

    /// <summary>
    /// The other half of the same rule: shares somebody stated never came from the bill, so
    /// completing the bill leaves them exactly as they were.
    /// </summary>
    [Fact]
    public async Task Completing_a_bill_on_an_expense_split_by_hand_leaves_its_shares()
    {
        var (expense, _, even) = await AnExpense();
        var bill = await Receipts.SaveForExpense(expense.Id, Bill(
            Item("Shared", 24m, even), Item("Dessert", 24m, null)), Ct);

        await Receipts.SetRule(expense.Id, bill.Items.Single(item => item.Name == "Dessert").Id,
            new SetReceiptItemRuleRequest { SplitRuleVersionId = even }, Ct);

        var split = Assert.Single((await Transactions.GetDetails(expense.Id, Ct))!.Splits);
        Assert.Equal(Self, split.UserId);
        Assert.Equal(48m, split.Amount);
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

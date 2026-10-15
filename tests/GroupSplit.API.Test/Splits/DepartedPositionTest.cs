using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// What may and may not happen to an expense once somebody it names has left the group.
/// </summary>
/// <remarks>
/// Somebody can only leave square, and the group's balances list only the people still in
/// it, so the column adds up for exactly as long as nothing changes what a leaver paid or
/// owed. Two things went wrong with that. Deleting, re-dividing or re-pricing an old expense
/// moved their part silently, leaving money nobody on the page could settle. And every other
/// edit -- a rename -- was refused, because the stored shares were checked against today's
/// members. Now the second always works and the first is refused by name.
/// <para>
/// Every test here ends by checking that the group's balances still sum to zero.
/// </para>
/// </remarks>
public class DepartedPositionTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupService Groups => GetService<IGroupService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private IReceiptService Receipts => GetService<IReceiptService>();
    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    private sealed record Scene(Guid GroupId, Guid ExpenseId, Guid Leaver, Guid CategoryId);

    /// <summary>
    /// What the next request would start from: nothing tracked but the signed-in account,
    /// which the services hold on to and expect the context to know.
    /// </summary>
    private void FreshRequest()
    {
        DbContext.ChangeTracker.Clear();
        DbContext.Attach(GetService<ICurrentUser>().User);
    }

    /// <summary>
    /// A group of two. 200 is spent and split evenly, the other member pays back their 100,
    /// and then is removed -- the real way, which is only allowed because they are square.
    /// </summary>
    /// <param name="leaverPaid">Whether the leaver paid the 200, rather than the caller.</param>
    private async Task<Scene> ASettledLeaver(bool leaverPaid = false)
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var leaver = await CreateNewUser();
        await JoinGroup(group.Id, leaver);

        var categoryId = await CreateEvenCategory(group.Id, "Food");

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 200m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            CategoryId = categoryId,
            PaidByUserId = leaverPaid ? leaver.Id : Self
        }, Ct);

        await GetService<ISettlementService>().Settle(group.Id, new SettleRequest
        {
            UserId = leaver.Id,
            Amount = 100m,
            Direction = leaverPaid ? SettlementDirection.YouPaidThem : SettlementDirection.TheyPaidYou
        }, Ct);

        await Groups.RemoveGroupMember(group.Id, leaver.Id, Ct);

        // What a fresh request sees.
        FreshRequest();

        return new Scene(group.Id, expense.Id, leaver.Id, categoryId);
    }

    /// <summary>
    /// The edit as the PATCH endpoint builds it: the stored expense, shares included, with
    /// the change applied on top.
    /// </summary>
    private async Task<Data.Entities.Transaction> Edit(Guid expenseId, Action<UpdateTransactionRequest> change)
    {
        var model = (await Transactions.GetUpdateModel(expenseId, Ct))!;
        change(model);

        return await Transactions.Update(expenseId, model, Ct);
    }

    private async Task<ConflictException> Refused(Func<Task> act)
    {
        var refused = await Assert.ThrowsAsync<ConflictException>(act);

        Assert.Equal(ErrorCodes.TransactionMovesDepartedBalance, refused.Code);

        // What the next request would see: nothing was written.
        FreshRequest();

        return refused;
    }

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>().AsNoTracking()
            .Where(split => split.TransactionId == transactionId)
            .ToListAsync(Ct);

    private async Task AssertBalancesSumToZero(Guid groupId)
    {
        FreshRequest();

        var balances = await (await Groups.GetGroupNetBalance(groupId, Ct)).ToListAsync(Ct);

        Assert.Equal(0m, balances.Sum(balance => balance.Balance));
    }

    private async Task AssertTheLeaverStillOwes(Scene scene, decimal owed)
    {
        var splits = await SplitsOf(scene.ExpenseId);
        Assert.Equal(owed, splits.Single(split => split.UserId == scene.Leaver).Amount);
    }

    // ---- What always works ---------------------------------------------------------------

    [Fact]
    public async Task Renaming_an_expense_a_leaver_shared_keeps_every_share()
    {
        var scene = await ASettledLeaver();

        await Edit(scene.ExpenseId, model => model.Name = "Dinner at Rosa's");

        var expense = await DbContext.Set<Expense>().AsNoTracking()
            .FirstAsync(candidate => candidate.Id == scene.ExpenseId, Ct);

        Assert.Equal("Dinner at Rosa's", expense.Name);
        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Re_noting_and_re_dating_an_expense_a_leaver_shared_moves_no_money()
    {
        var scene = await ASettledLeaver();
        var lastWeek = DateTimeOffset.UtcNow.AddDays(-7);

        await Edit(scene.ExpenseId, model =>
        {
            model.Description = "The one with the birthday cake";
            model.DateTime = lastWeek;
        });

        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Re_filing_an_expense_a_leaver_shared_under_another_category_keeps_their_share()
    {
        var scene = await ASettledLeaver();
        var drinks = await CreateEvenCategory(scene.GroupId, "Drinks");

        await Edit(scene.ExpenseId, model => model.CategoryId = drinks);

        var expense = await DbContext.Set<Expense>().AsNoTracking()
            .FirstAsync(candidate => candidate.Id == scene.ExpenseId, Ct);

        Assert.Equal(drinks, expense.CategoryId);
        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Renaming_an_expense_a_leaver_paid_for_keeps_them_as_payer()
    {
        var scene = await ASettledLeaver(leaverPaid: true);

        await Edit(scene.ExpenseId, model => model.Name = "Their dinner");

        var expense = await DbContext.Set<Expense>().AsNoTracking()
            .FirstAsync(candidate => candidate.Id == scene.ExpenseId, Ct);

        Assert.Equal(scene.Leaver, expense.UserId);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    /// <summary>
    /// The deliberate path. The amount can change as long as the leaver's share does not:
    /// whoever is still here takes the difference, and says so by setting the shares.
    /// </summary>
    [Fact]
    public async Task An_amount_edit_that_keeps_the_leavers_share_and_moves_the_rest_is_accepted()
    {
        var scene = await ASettledLeaver();

        await Edit(scene.ExpenseId, model =>
        {
            model.Amount = 260m;
            model.Splits =
            [
                new SplitInput { UserId = Self, Amount = 160m },
                new SplitInput { UserId = scene.Leaver, Amount = 100m }
            ];
        });

        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    /// <summary>
    /// The control: a leaver who holds nothing on an expense is no reason to refuse it.
    /// </summary>
    [Fact]
    public async Task A_members_share_can_still_move_when_a_leaver_holds_nothing_on_the_expense()
    {
        var scene = await ASettledLeaver();

        var mine = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "After they left",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = scene.GroupId,
            CategoryId = scene.CategoryId
        }, Ct);

        await Edit(mine.Id, model => model.Amount = 55m);
        await Transactions.Delete(mine.Id, Ct);

        await AssertBalancesSumToZero(scene.GroupId);
    }

    // ---- What is refused, by name --------------------------------------------------------

    [Fact]
    public async Task Changing_the_amount_of_an_expense_a_leaver_shared_is_refused_and_names_them()
    {
        var scene = await ASettledLeaver();

        var refused = await Refused(() => Edit(scene.ExpenseId, model =>
        {
            model.Amount = 300m;
            model.Splits = null;
        }));

        Assert.Equal([scene.Leaver], Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["userIds"]));
        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Changing_the_amount_of_an_expense_a_leaver_paid_for_is_refused()
    {
        var scene = await ASettledLeaver(leaverPaid: true);

        await Refused(() => Edit(scene.ExpenseId, model =>
        {
            model.Amount = 260m;
            model.Splits =
            [
                new SplitInput { UserId = Self, Amount = 160m },
                new SplitInput { UserId = scene.Leaver, Amount = 100m }
            ];
        }));

        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Handing_what_a_leaver_paid_for_to_a_member_is_refused()
    {
        var scene = await ASettledLeaver(leaverPaid: true);

        await Refused(() => Edit(scene.ExpenseId, model => model.PaidByUserId = Self));

        var expense = await DbContext.Set<Expense>().AsNoTracking()
            .FirstAsync(candidate => candidate.Id == scene.ExpenseId, Ct);

        Assert.Equal(scene.Leaver, expense.UserId);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Dividing_again_an_expense_a_leaver_shared_is_refused_rather_than_handing_their_share_to_the_rest()
    {
        var scene = await ASettledLeaver();

        await Refused(() => Edit(scene.ExpenseId, model => model.Splits = null));

        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Moving_an_expense_a_leaver_shared_to_another_group_is_refused()
    {
        var scene = await ASettledLeaver();
        var elsewhere = await Groups.CreateGroup(new CreateGroupRequest { Name = "Elsewhere" }, Ct);

        await Refused(() => Edit(scene.ExpenseId, model =>
        {
            model.GroupId = elsewhere.Id;
            model.CategoryId = null;
            model.Splits = null;
        }));

        await AssertTheLeaverStillOwes(scene, 100m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    /// <summary>
    /// The shares a move carries would otherwise read as the expense's own holders in a group
    /// they were never in, and slip past the check for strangers.
    /// </summary>
    [Fact]
    public async Task Moving_an_expense_while_stating_shares_for_the_old_groups_people_is_refused()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var flatmate = await CreateNewUser();
        await JoinGroup(group.Id, flatmate);

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 50m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id
        }, Ct);

        var elsewhere = await Groups.CreateGroup(new CreateGroupRequest { Name = "Elsewhere" }, Ct);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => Edit(expense.Id, model =>
        {
            model.GroupId = elsewhere.Id;
            model.CategoryId = null;
            // Different from what it holds, so this is a statement and not silence.
            model.Splits =
            [
                new SplitInput { UserId = Self, Amount = 30m },
                new SplitInput { UserId = flatmate.Id, Amount = 20m }
            ];
        }));

        Assert.Equal(ErrorCodes.SplitUserNotInGroup, refused.Code);
    }

    [Fact]
    public async Task Deleting_an_expense_a_leaver_shared_is_refused()
    {
        var scene = await ASettledLeaver();

        var refused = await Refused(() => Transactions.Delete(scene.ExpenseId, Ct));

        Assert.Equal([scene.Leaver], Assert.IsAssignableFrom<IReadOnlyList<Guid>>(refused.Extensions["userIds"]));
        Assert.NotEmpty(await SplitsOf(scene.ExpenseId));
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Deleting_an_expense_a_leaver_paid_for_is_refused()
    {
        var scene = await ASettledLeaver(leaverPaid: true);

        await Refused(() => Transactions.Delete(scene.ExpenseId, Ct));

        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Deleting_the_settlement_that_let_a_member_leave_is_refused()
    {
        var scene = await ASettledLeaver();

        var settlement = await DbContext.Set<Transfer>().AsNoTracking()
            .FirstAsync(transfer => transfer.GroupId == scene.GroupId, Ct);

        await Refused(() => Transactions.Delete(settlement.Id, Ct));

        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Correcting_the_date_of_the_settlement_that_let_a_member_leave_still_works()
    {
        var scene = await ASettledLeaver();

        var settlement = await DbContext.Set<Transfer>().AsNoTracking()
            .FirstAsync(transfer => transfer.GroupId == scene.GroupId, Ct);

        await Edit(settlement.Id, model => model.DateTime = DateTimeOffset.UtcNow.AddDays(-2));

        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Previewing_an_edit_that_would_move_a_leavers_share_refuses_the_way_the_save_does()
    {
        var scene = await ASettledLeaver();

        // "Automatically" in the dialog: no shares stated, divide it again.
        var model = (await Transactions.GetUpdateModel(scene.ExpenseId, Ct))!;
        model.Amount = 300m;
        model.Splits = null;

        await Refused(() => Transactions.PreviewUpdate(scene.ExpenseId, model, redivide: true, ct: Ct));

        // And a rename previews, as it saves.
        var rename = (await Transactions.GetUpdateModel(scene.ExpenseId, Ct))!;
        rename.Name = "Renamed";

        var preview = await Transactions.PreviewUpdate(scene.ExpenseId, rename, ct: Ct);

        Assert.Equal(100m, preview.Splits.Single(split => split.UserId == scene.Leaver).Amount);
    }

    // ---- A bill whose line names a leaver --------------------------------------------------

    /// <summary>
    /// A bill of 60 divided line by line: the caller's own 30, and 30 of wine that was the
    /// other member's. They pay their 30 back and are removed.
    /// </summary>
    private async Task<(Scene Scene, Guid WineLine, Guid Mine)> ABillWithALeaversLine()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Dinner" }, Ct);
        var leaver = await CreateNewUser();
        await JoinGroup(group.Id, leaver);

        var bill = await DbContext.Set<SplitRuleVersion>()
            .Include(version => version.SplitRule)
            .FirstAsync(version => version.SplitRule.Group.Id == group.Id
                                   && version.SupersededAt == null
                                   && version is ItemizedSplitRuleVersion, Ct);

        var food = await GetService<ICategoryService>().Create(new CreateCategoryRequest
            { GroupId = group.Id, Name = "Food", DefaultSplitRuleId = bill.SplitRuleId }, Ct);

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner", Amount = 60m, DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id, CategoryId = food.Id,
            Splits = [new SplitInput { UserId = Self, Amount = 60m }]
        }, Ct);

        var mine = (await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group.Id, Name = "Mine", Definition = new SoleSplitRuleDto(Self) }, Ct)).Current!.Id;
        var theirs = (await Rules.Create(new CreateSplitRuleRequest
            { GroupId = group.Id, Name = "Theirs", Definition = new SoleSplitRuleDto(leaver.Id) }, Ct)).Current!.Id;

        var receipt = await Receipts.SaveForExpense(expense.Id, new SaveReceiptRequest
        {
            Items =
            [
                new ReceiptItemInput { Name = "Pasta", UnitPrice = 30m, TotalPrice = 30m, SplitRuleVersionId = mine },
                new ReceiptItemInput { Name = "Wine", UnitPrice = 30m, TotalPrice = 30m, SplitRuleVersionId = theirs }
            ],
            Subtotal = 60m,
            Tax = 0m,
            Total = 60m
        }, Ct);

        await Receipts.Divide(expense.Id, Ct);

        await GetService<ISettlementService>().Settle(group.Id, new SettleRequest { UserId = leaver.Id, Amount = 30m }, Ct);
        await Groups.RemoveGroupMember(group.Id, leaver.Id, Ct);

        FreshRequest();

        var wine = receipt.Items.Single(item => item.Name == "Wine").Id;

        return (new Scene(group.Id, expense.Id, leaver.Id, food.Id), wine, mine);
    }

    [Fact]
    public async Task A_bill_line_naming_a_leaver_can_be_pointed_at_another_rule_without_moving_their_share()
    {
        var (scene, wine, mine) = await ABillWithALeaversLine();

        await AssertTheLeaverStillOwes(scene, 30m);

        await Receipts.SetRule(scene.ExpenseId, wine, new SetReceiptItemRuleRequest { SplitRuleVersionId = mine }, Ct);

        FreshRequest();
        var receipt = await Receipts.ForExpense(scene.ExpenseId, Ct);

        Assert.Equal(mine, receipt.Items.Single(item => item.Id == wine).SplitRuleVersionId);
        await AssertTheLeaverStillOwes(scene, 30m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Dividing_by_the_bill_after_re_pointing_a_leavers_line_is_refused_and_the_bill_says_it_cannot_divide()
    {
        var (scene, wine, mine) = await ABillWithALeaversLine();

        await Receipts.SetRule(scene.ExpenseId, wine, new SetReceiptItemRuleRequest { SplitRuleVersionId = mine }, Ct);
        FreshRequest();

        await Refused(() => Receipts.Divide(scene.ExpenseId, Ct));
        await Refused(() => Receipts.Preview(scene.ExpenseId, Ct));

        var receipt = await Receipts.ForExpense(scene.ExpenseId, Ct);
        Assert.False((await Receipts.ResponseFor(receipt, ct: Ct)).CanDivide);

        await AssertTheLeaverStillOwes(scene, 30m);
        await AssertBalancesSumToZero(scene.GroupId);
    }

    [Fact]
    public async Task Renaming_an_expense_whose_bill_names_a_leaver_still_works()
    {
        var (scene, _, _) = await ABillWithALeaversLine();

        await Edit(scene.ExpenseId, model => model.Name = "Dinner at Rosa's");

        await AssertTheLeaverStillOwes(scene, 30m);
        await AssertBalancesSumToZero(scene.GroupId);
    }
}

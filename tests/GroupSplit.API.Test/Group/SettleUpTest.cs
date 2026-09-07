using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Settling a group up in one action.
/// </summary>
/// <remarks>
/// The property nearly every test here is really about: sweeping a set includes sweeping the
/// payments written to settle it, so the all-time balance and the outstanding balance are
/// the same number no matter how anybody scopes a run. That is what lets the balance query,
/// the leave-group check and the delete-account check carry on unchanged.
/// </remarks>
public class SettleUpTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// The whole point, in one test: two people, one unequal expense, one action, and
    /// nobody owes anybody afterwards.
    /// </summary>
    [Fact]
    public async Task SettleUp_ZeroesTheGroup()
    {
        var (group, other) = await GroupOfTwo();

        // The caller pays 100 for a dinner split evenly, so the other member owes them 50.
        await Expense(group, 100);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var payment = Assert.Single(run.Payments);

        Assert.Equal(other.Id, payment.FromUserId);
        Assert.Equal(50, payment.Amount);

        Assert.Equal(0, await BalanceOf(group, other.Id));
        Assert.Equal(0, await BalanceOf(group, Me.Id));
    }

    /// <summary>
    /// The invariant stated directly. A run writes transfers and marks them swept along with
    /// everything else, so what is outstanding afterwards is empty -- and the balance query,
    /// which knows nothing about runs, still says zero.
    /// </summary>
    [Fact]
    public async Task SettleUp_SweepsItsOwnPayments()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var outstanding = await DbContext.Set<Data.Entities.Transaction>()
            .Where(transaction => transaction.GroupId == group && transaction.SettlementRunId == null)
            .ToListAsync(Ct);

        Assert.Empty(outstanding);

        // Including the transfer it wrote, which is the half that is easy to forget.
        var transfer = await DbContext.Set<Transfer>().SingleAsync(row => row.GroupId == group, Ct);

        Assert.NotNull(transfer.SettlementRunId);
    }

    /// <summary>
    /// Running it again over the same group asks for nothing, because there is nothing left
    /// unswept. This is the test that would catch a run paying the same expense twice.
    /// </summary>
    [Fact]
    public async Task SettleUp_Twice_HasNothingLeftToSettle()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var again = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleUp(group, new SettleUpRequest(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementNothingToSettle, again.Code);
    }

    /// <summary>
    /// An end date settles what came before it and leaves the rest alone -- the month-end
    /// case, and the reason the scope exists.
    /// </summary>
    [Fact]
    public async Task SettleUp_ScopedByDate_LeavesLaterExpensesOutstanding()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100, on: new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await Expense(group, 40, on: new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero));

        var endOfSeptember = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        var run = await Settlements().SettleUp(group,
            new SettleUpRequest { Scope = new TransactionFilter(To: endOfSeptember) }, Ct);

        // Only September's 100 was swept, so only its 50 was paid.
        Assert.Equal(50, Assert.Single(run.Payments).Amount);

        // October's 40 is untouched, so the other member still owes 20 of it.
        Assert.Equal(-20, await BalanceOf(group, other.Id));

        var october = await DbContext.Set<Expense>()
            .SingleAsync(expense => expense.GroupId == group && expense.Amount == 40, Ct);

        Assert.Null(october.SettlementRunId);
    }

    /// <summary>
    /// A run scoped to the end of September dates its payments 30 September, without being
    /// told twice. A settlement recorded in October for September otherwise leaves the month
    /// it closed not containing the payments that closed it.
    /// </summary>
    [Fact]
    public async Task SettleUp_DatesItsPaymentsAtTheEndOfTheScope()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, on: new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

        var endOfSeptember = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        var run = await Settlements().SettleUp(group,
            new SettleUpRequest { Scope = new TransactionFilter(To: endOfSeptember) }, Ct);

        Assert.Equal(endOfSeptember, run.EffectiveDate);

        var transfer = await DbContext.Set<Transfer>().SingleAsync(row => row.GroupId == group, Ct);

        Assert.Equal(endOfSeptember, transfer.DateTime);
    }

    /// <summary>
    /// A repayment somebody already recorded by hand is money that has already moved, so a
    /// run has to count it. Ignoring it would ask for it a second time.
    /// </summary>
    [Fact]
    public async Task SettleUp_ConsumesRepaymentsAlreadyRecorded()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        // The other member has already handed over 30 of the 50 they owe.
        await GetService<IGroupService>().Settle(group,
            new SettleRequest { UserId = other.Id, Amount = 30 }, Ct);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Equal(20, Assert.Single(run.Payments).Amount);
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// The case a settled-period model has to answer and this one answers by doing nothing:
    /// an expense back-dated into a month already settled is simply outstanding, so the next
    /// run picks it up. No reopening and no correction entry.
    /// </summary>
    [Fact]
    public async Task SettleUp_ExpenseBackDatedIntoASettledPeriod_IsPickedUpByTheNextRun()
    {
        var (group, other) = await GroupOfTwo();

        var midSeptember = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var endOfSeptember = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);

        await Expense(group, 100, on: midSeptember);

        await Settlements().SettleUp(group,
            new SettleUpRequest { Scope = new TransactionFilter(To: endOfSeptember) }, Ct);

        Assert.Equal(0, await BalanceOf(group, other.Id));

        // Somebody remembers a September taxi in October and files it under its own date.
        await Expense(group, 60, on: midSeptember);

        var second = await Settlements().SettleUp(group,
            new SettleUpRequest { Scope = new TransactionFilter(To: endOfSeptember) }, Ct);

        Assert.Equal(30, Assert.Single(second.Payments).Amount);
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// The scope is the expense listing's filter, so anything it can narrow, a settling-up
    /// can settle. Category is the one that proves the point: nothing about the run knows
    /// what a category is.
    /// </summary>
    [Fact]
    public async Task SettleUp_ScopedByCategory_SettlesOnlyThatCategory()
    {
        var (group, other) = await GroupOfTwo();

        var groceries = await CreateEvenCategory(group, "Groceries");

        await Expense(group, 100, category: groceries);
        await Expense(group, 40);

        var run = await Settlements().SettleUp(group,
            new SettleUpRequest { Scope = new TransactionFilter(Category: "groceries") }, Ct);

        Assert.Equal(50, Assert.Single(run.Payments).Amount);

        // The uncategorised 40 is still open, so 20 of it is still owed.
        Assert.Equal(-20, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// A group that is already square still has a period to close: the run sweeps the rows so
    /// they stop being offered, and moves no money doing it.
    /// </summary>
    [Fact]
    public async Task SettleUp_WhenAlreadySquare_SweepsWithoutPaying()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);
        await Expense(group, 100, paidBy: other);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Empty(run.Payments);
        Assert.Equal(2, run.TransactionCount);

        Assert.Empty(await DbContext.Set<Data.Entities.Transaction>()
            .Where(transaction => transaction.GroupId == group && transaction.SettlementRunId == null)
            .ToListAsync(Ct));
    }

    [Fact]
    public async Task SettleUp_WithNothingOutstanding_Throws()
    {
        var (group, _) = await GroupOfTwo();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().SettleUp(group, new SettleUpRequest(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementNothingToSettle, exception.Code);
    }

    [Fact]
    public async Task SettleUp_GroupNotFound_Throws()
    {
        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements().SettleUp(Guid.NewGuid(), new SettleUpRequest(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.GroupNotFound, exception.Code);
    }

    /// <summary>
    /// Unnamed runs are named after what they cover, so a month's worth of expenses proposes
    /// the month rather than "Settle-up (3)".
    /// </summary>
    [Fact]
    public async Task SettleUp_LabelsItselfAfterTheDatesItCovers()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, on: new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero));
        await Expense(group, 60, on: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        Assert.Equal("September 2026", run.Label);

        // And the payments it wrote carry it, so the activity list can say which settling-up
        // a transfer belongs to without anybody describing it one payment at a time.
        var transfer = await DbContext.Set<Transfer>().SingleAsync(row => row.GroupId == group, Ct);

        Assert.Equal("September 2026", transfer.Description);
    }

    [Fact]
    public async Task SettleUp_WithALabel_KeepsIt()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        var run = await Settlements().SettleUp(group,
            new SettleUpRequest { Label = "  Lisbon trip  " }, Ct);

        Assert.Equal("Lisbon trip", run.Label);
    }

    /// <summary>
    /// A preview is a question, not an instruction. Nothing about the group may differ for
    /// having asked it.
    /// </summary>
    [Fact]
    public async Task Preview_RecordsNothing()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        var preview = await Settlements().Preview(group, null, Ct);

        Assert.Equal(50, Assert.Single(preview.Payments).Amount);
        Assert.Equal(1, preview.TransactionCount);
        Assert.Equal(100, preview.Total);

        Assert.Empty(await DbContext.Set<Transfer>().Where(row => row.GroupId == group).ToListAsync(Ct));
        Assert.Empty(await DbContext.Set<SettlementRun>().ToListAsync(Ct));

        // And the balance is where it was: the other member still owes their half.
        Assert.Equal(-50, await BalanceOf(group, other.Id));
    }

    [Fact]
    public async Task Preview_WithNothingOutstanding_IsEmptyRatherThanAnError()
    {
        var (group, _) = await GroupOfTwo();

        var preview = await Settlements().Preview(group, null, Ct);

        Assert.Empty(preview.Payments);
        Assert.Equal(0, preview.TransactionCount);
        Assert.Null(preview.CoversFrom);
    }

    // ---- editing what has been settled -----------------------------------------------

    /// <summary>
    /// The one way this model could go quietly wrong, closed off. A settled expense that
    /// could still be edited would leave the balances showing a difference -- they read
    /// every row, settled or not -- while settling up said there was nothing to settle,
    /// because everything that could pay it off was already marked done.
    /// </summary>
    [Fact]
    public async Task EditingASettledExpense_IsRefused()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var expense = await DbContext.Set<Expense>().SingleAsync(row => row.GroupId == group, Ct);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            GetService<ITransactionService>().Update(expense.Id, new UpdateTransactionRequest
            {
                GroupId = group,
                PaidByUserId = Me.Id,
                Name = "Dinner",
                Amount = 120,
                DateTime = expense.DateTime
            }, Ct).AsTask());

        Assert.Equal(Shared.Errors.ErrorCodes.TransactionSettled, exception.Code);

        // Named, so the client can offer to undo the one it means rather than sending
        // somebody looking for it.
        Assert.Equal(run.Id, Assert.IsType<Guid>(exception.Extensions["settlementRunId"]));
    }

    [Fact]
    public async Task DeletingASettledExpense_IsRefused()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var expense = await DbContext.Set<Expense>().SingleAsync(row => row.GroupId == group, Ct);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            GetService<ITransactionService>().Delete(expense.Id, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.TransactionSettled, exception.Code);
    }

    /// <summary>
    /// The whole point of undoing: correct the bill, settle again, and nobody is asked twice
    /// for what they have already handed over.
    /// </summary>
    [Fact]
    public async Task Undo_ThenEdit_ThenSettleAgain_AsksOnlyForTheDifference()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        // The other member has paid their 50.
        Assert.Equal(50, Assert.Single(run.Payments).Amount);

        await Settlements().Reopen(group, run.Id, Ct);

        // Undoing moves no money and therefore no balance. The payment really happened and
        // is still in the ledger; what it takes back is the claim that it settled anything.
        Assert.Equal(0, await BalanceOf(group, other.Id));

        var expense = await DbContext.Set<Expense>().SingleAsync(row => row.GroupId == group, Ct);

        await GetService<ITransactionService>().Update(expense.Id, new UpdateTransactionRequest
        {
            GroupId = group,
            PaidByUserId = Me.Id,
            Name = "Dinner",
            Amount = 120,
            DateTime = expense.DateTime
        }, Ct);

        var second = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        // 60 is their new share and 50 of it is already paid, so 10 is what is left.
        Assert.Equal(10, Assert.Single(second.Payments).Amount);
        Assert.Equal(0, await BalanceOf(group, other.Id));
    }

    /// <summary>
    /// Undoing does not take the money back. The transfer stays in the ledger -- it really
    /// happened -- and goes back to being outstanding, which is what lets the next run count
    /// it.
    /// </summary>
    [Fact]
    public async Task Undo_KeepsThePaymentsAndPutsThemBackToOutstanding()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        var reopened = await Settlements().Reopen(group, run.Id, Ct);

        Assert.NotNull(reopened.ReopenedAt);

        var transfer = await DbContext.Set<Transfer>().SingleAsync(row => row.GroupId == group, Ct);

        Assert.Equal(50, transfer.Amount);
        Assert.Null(transfer.SettlementRunId);

        // Whose payment it was survives the undoing: it was still written by that run.
        Assert.Equal(run.Id, transfer.WrittenByRunId);
    }

    [Fact]
    public async Task Undo_Twice_IsRefused()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100);

        var run = await Settlements().SettleUp(group, new SettleUpRequest(), Ct);

        await Settlements().Reopen(group, run.Id, Ct);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            Settlements().Reopen(group, run.Id, Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementRunAlreadyReopened, exception.Code);
    }

    [Fact]
    public async Task Undo_ARunFromAnotherGroup_IsNotFound()
    {
        var (group, _) = await GroupOfTwo();

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            Settlements().Reopen(group, Guid.NewGuid(), Ct));

        Assert.Equal(Shared.Errors.ErrorCodes.SettlementRunNotFound, exception.Code);
    }

    /// <summary>
    /// The listing is where an id to undo comes from, and it reports the payments the run
    /// wrote -- not the ones it merely swept.
    /// </summary>
    [Fact]
    public async Task List_ReportsWhatEachRunWroteRatherThanWhatItSwept()
    {
        var (group, other) = await GroupOfTwo();

        await Expense(group, 100);

        // A hand-recorded repayment the run will consume. It is money that moved, but it is
        // not a payment this run made.
        await GetService<IGroupService>().Settle(group,
            new SettleRequest { UserId = other.Id, Amount = 30 }, Ct);

        await Settlements().SettleUp(group, new SettleUpRequest { Label = "September" }, Ct);

        var runs = await Settlements().List(group);

        var listed = Assert.Single(runs);

        Assert.Equal("September", listed.Label);
        Assert.Equal(20, Assert.Single(listed.Payments).Amount);

        // Three rows swept: the expense, the repayment it consumed, and the payment it wrote.
        Assert.Equal(3, listed.TransactionCount);

        // The spending it settled, with the repayments left out of it.
        Assert.Equal(100, listed.Total);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private ISettlementService Settlements() => GetService<ISettlementService>();

    private async Task<(Guid Group, Data.Entities.User Other)> GroupOfTwo()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Settle Up Group" }, Ct);

        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        return (group.Id, other);
    }

    /// <summary>
    /// An expense in the group, split evenly, paid by the caller unless somebody else is
    /// named.
    /// </summary>
    private async Task Expense(Guid groupId, decimal amount, DateTimeOffset? on = null,
        Guid? category = null, Data.Entities.User? paidBy = null)
    {
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = groupId,
            CategoryId = category,
            PaidByUserId = paidBy?.Id ?? Me.Id,
            Name = "Dinner",
            Amount = amount,
            DateTime = on ?? DateTimeOffset.UtcNow
        }, Ct);
    }

    private async Task<decimal> BalanceOf(Guid groupId, Guid userId)
    {
        var balances = await GetService<IGroupService>().GetGroupNetBalance(groupId, Ct);

        return await balances
            .Where(balance => balance.UserId == userId)
            .Select(balance => balance.Balance)
            .FirstAsync(Ct);
    }
}

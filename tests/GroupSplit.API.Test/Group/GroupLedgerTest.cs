using GroupSplit.API.Endpoints;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// A group's ledger: everything that moved money in it, filtered by kind, with each row
/// carrying the reader's share and their balance as at that entry.
/// </summary>
/// <remarks>
/// One listing where the app had two tabs. Expenses and Activity were the same rows with
/// different filters, and splitting them kept the group's spend apart from the group's
/// balance history -- which is why neither could show a running balance. That column is the
/// reason this is a merge rather than a rename, and it is what most of these are about.
/// </remarks>
public class GroupLedgerTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// Everything is the default, because "what has happened here" does not distinguish.
    /// </summary>
    [Fact]
    public async Task Unfiltered_TheLedgerHoldsExpensesAndSettlementsTogether()
    {
        var (group, friend) = await GroupOfTwo();

        await Expense(group, 100, "Dinner");
        await Settle(group, friend, 50);

        var entries = await Ledger(group);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Kind is ActivityKind.Expense);
        Assert.Contains(entries, entry => entry.Kind is ActivityKind.Transfer);
    }

    [Theory]
    [InlineData(ActivityKind.Expense)]
    [InlineData(ActivityKind.Transfer)]
    public async Task FilteredByKind_TheLedgerHoldsOnlyThatKind(ActivityKind kind)
    {
        var (group, friend) = await GroupOfTwo();

        await Expense(group, 100, "Dinner");
        await Settle(group, friend, 50);

        var entries = await Ledger(group, new ActivityFilter(Kind: kind));

        Assert.All(entries, entry => Assert.Equal(kind, entry.Kind));
        Assert.Single(entries);
    }

    [Fact]
    public async Task AnExpense_CarriesTheCategoryAndCategoryId()
    {
        var (group, _) = await GroupOfTwo();
        var categoryId = await CreateEvenCategory(group, "Food");

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            PaidByUserId = Me.Id,
            Name = "Dinner",
            Amount = 100,
            DateTime = DateTimeOffset.UtcNow,
            CategoryId = categoryId
        }, Ct);

        var entry = Assert.Single(await Ledger(group));

        Assert.Equal(categoryId, entry.CategoryId);
        Assert.Equal("Food", entry.Category);
    }

    [Fact]
    public async Task AnExpense_InGroupActivity_CarriesTheCategoryAndCategoryId()
    {
        var (group, _) = await GroupOfTwo();
        var categoryId = await CreateEvenCategory(group, "Food");

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            PaidByUserId = Me.Id,
            Name = "Dinner",
            Amount = 100,
            DateTime = DateTimeOffset.UtcNow,
            CategoryId = categoryId
        }, Ct);

        var activity = await GetService<IGroupService>().GetGroupActivity(group, Ct);
        var entry = Assert.Single(await activity.SelectActivityDto(Me.Id).ToListAsync(Ct));

        Assert.Equal(categoryId, entry.CategoryId);
        Assert.Equal("Food", entry.Category);
    }

    [Fact]
    public async Task AnExpense_InUserActivity_CarriesTheCategoryAndCategoryId()
    {
        var (group, _) = await GroupOfTwo();
        var categoryId = await CreateEvenCategory(group, "Food");

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            PaidByUserId = Me.Id,
            Name = "Dinner",
            Amount = 100,
            DateTime = DateTimeOffset.UtcNow,
            CategoryId = categoryId
        }, Ct);

        var entry = Assert.Single(await DbContext.Set<Data.Entities.Transaction>()
            .SelectUserActivityDto(Me.Id)
            .ToListAsync(Ct));

        Assert.Equal(categoryId, entry.CategoryId);
        Assert.Equal("Food", entry.Category);
    }

    /// <summary>
    /// What the dinner cost and what it cost you are different numbers, and no listing in
    /// the app carried the second.
    /// </summary>
    [Fact]
    public async Task AnExpense_CarriesTheReadersOwnShare()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, "Dinner");

        var entry = Assert.Single(await Ledger(group));

        Assert.Equal(100, entry.Amount);
        Assert.Equal(50, entry.Share);
    }

    /// <summary>
    /// Null rather than zero, and the two are different answers. Paying somebody back is not
    /// a cost anyone carries a part of, and a row saying 0.00 would be claiming the reader's
    /// part of it was nothing.
    /// </summary>
    [Fact]
    public async Task ASettlement_CarriesNoShareAtAll()
    {
        var (group, friend) = await GroupOfTwo();

        await Expense(group, 100, "Dinner");
        await Settle(group, friend, 50);

        var transfer = Assert.Single(await Ledger(group, new ActivityFilter(Kind: ActivityKind.Transfer)));

        Assert.Null(transfer.Share);
    }

    /// <summary>
    /// The reason to merge rather than rename. The balance moves on both kinds of entry, so
    /// a history of it cannot be drawn against a list that hides half of them.
    /// </summary>
    [Fact]
    public async Task TheRunningBalance_MovesOnExpensesAndOnSettlementsAlike()
    {
        var (group, friend) = await GroupOfTwo();

        // The caller pays 100 split evenly: they are owed 50.
        await Expense(group, 100, "Dinner", at: Day(1));

        // The friend pays them 30 back: they are owed 20.
        await Settle(group, friend, 30, at: Day(2));

        // The friend pays 60 split evenly: the caller owes 30 of it, so is owed 20 - 30.
        await Expense(group, 60, "Taxi", paidBy: friend, at: Day(3));

        var entries = await Ledger(group);

        // Newest first, which is how a ledger is read.
        Assert.Equal([-10m, 20m, 50m], entries.Select(entry => entry.RunningBalance));
    }

    /// <summary>
    /// A balance as at a date is what it is whether or not the rows above it are on screen.
    /// Narrowing the list to settlements must not rewrite history.
    /// </summary>
    [Fact]
    public async Task TheRunningBalance_IgnoresWhateverTheListIsFilteredTo()
    {
        var (group, friend) = await GroupOfTwo();

        await Expense(group, 100, "Dinner", at: Day(1));
        await Settle(group, friend, 30, at: Day(2));

        var settlements = await Ledger(group, new ActivityFilter(Kind: ActivityKind.Transfer));

        // 50 owed after the dinner, 20 after the repayment -- and the repayment is the only
        // row on screen.
        Assert.Equal(20m, Assert.Single(settlements).RunningBalance);
    }

    /// <summary>
    /// Both parties, not just the payer: "Omar paid you" is a row somebody looks for by the
    /// name of whoever was paid as readily as by the name of whoever paid.
    /// </summary>
    [Fact]
    public async Task Searching_MatchesEitherEndOfASettlement()
    {
        var (group, friend) = await GroupOfTwo();

        // The friend pays for the dinner, so the caller owes them -- and then pays them
        // back, which puts the friend on the *receiving* end of the settlement. That is the
        // half a payer-only search would miss.
        await Expense(group, 100, "Dinner", paidBy: friend);
        await Repay(group, friend, 50);

        var byPayee = await Ledger(group, new ActivityFilter(Search: "loraine"));

        // Two rows name them: the expense they paid for and the settlement they received.
        Assert.Equal(2, byPayee.Count);
        Assert.Contains(byPayee, entry => entry.Kind is ActivityKind.Transfer);

        // And a search for nobody in the group finds nothing at all.
        Assert.Empty(await Ledger(group, new ActivityFilter(Search: "sofia")));
    }

    [Fact]
    public async Task Searching_MatchesAnExpenseByName()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, "Dinner");
        await Expense(group, 20, "Taxi");

        var found = await Ledger(group, new ActivityFilter(Search: "din"));

        Assert.Equal("Dinner", Assert.Single(found).Name);
    }

    [Fact]
    public async Task ADateRange_KeepsOnlyWhatFallsInsideIt()
    {
        var (group, _) = await GroupOfTwo();

        await Expense(group, 100, "Old", at: Day(1));
        await Expense(group, 20, "New", at: Day(10));

        var found = await Ledger(group, new ActivityFilter(From: Day(5)));

        Assert.Equal("New", Assert.Single(found).Name);
    }

    /// <summary>
    /// The figure above the list counts expenses whatever the list is filtered to. It is
    /// what makes one tab safe: a settlement is money changing hands, not money spent, and a
    /// total that mixed them would be wrong in a way nobody would catch.
    /// </summary>
    [Fact]
    public async Task TheGroupsSpendingTotal_LeavesSettlementsOut()
    {
        var (group, friend) = await GroupOfTwo();

        await Expense(group, 100, "Dinner");
        await Settle(group, friend, 50);

        var expenses = await GetService<ITransactionService>().InGroup(group, Ct);
        var summary = await expenses.ToSummaryAsync(new TransactionFilter(), Ct);

        Assert.Equal(1, summary.Count);
        Assert.Equal(100, summary.Total);
    }

    // ---- setup -----------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private static DateTimeOffset Day(int day) => new(2026, 9, day, 12, 0, 0, TimeSpan.Zero);

    private async Task<(Guid Group, Data.Entities.User Friend)> GroupOfTwo()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Ledger Group" }, Ct);

        var friend = await CreateNewUser();

        // The fixture provisions accounts with no name, and half of what a ledger search
        // has to reach is a person's name. Given one here, through this test's own context
        // -- the entity came from a scope of its own and is not tracked by it.
        var tracked = await DbContext.Set<Data.Entities.User>()
            .FirstAsync(candidate => candidate.Id == friend.Id, Ct);

        tracked.FirstName = "Loraine";
        tracked.LastName = "Monteagudo";
        await DbContext.SaveChangesAsync(Ct);

        await JoinGroup(group.Id, friend);

        return (group.Id, friend);
    }

    /// <summary>The listing exactly as the endpoint assembles it.</summary>
    private async Task<List<GroupLedgerEntryResponse>> Ledger(Guid group, ActivityFilter? filter = null)
    {
        var activity = await GetService<IGroupService>().GetGroupActivity(group, Ct);

        return await activity
            .ApplyFilter(filter)
            .ApplySort(new SortRequest(), GroupApi.ActivitySort)
            .SelectLedgerDto(Me.Id, activity)
            .ToListAsync(Ct);
    }

    private Task Expense(Guid groupId, decimal amount, string name,
        Data.Entities.User? paidBy = null, DateTimeOffset? at = null) =>
        GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = groupId,
            PaidByUserId = paidBy?.Id ?? Me.Id,
            Name = name,
            Amount = amount,
            DateTime = at ?? DateTimeOffset.UtcNow
        }, Ct).AsTask();

    /// <summary>A repayment from <paramref name="friend"/> to the caller.</summary>
    private Task Settle(Guid groupId, Data.Entities.User friend, decimal amount, DateTimeOffset? at = null) =>
        GetService<IGroupService>().Settle(groupId,
            new SettleRequest { UserId = friend.Id, Amount = amount, Date = at }, Ct);

    /// <summary>The other direction: the caller paying <paramref name="friend"/> back.</summary>
    private Task Repay(Guid groupId, Data.Entities.User friend, decimal amount, DateTimeOffset? at = null) =>
        GetService<IGroupService>().Settle(groupId,
            new SettleRequest
            {
                UserId = friend.Id,
                Amount = amount,
                Date = at,
                Direction = SettlementDirection.YouPaidThem
            }, Ct);
}

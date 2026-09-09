using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// The "your share" listing: what the caller owes on an expense, whoever paid for it.
/// </summary>
/// <remarks>
/// The half of the ledger the app could not show. <c>GET /transactions</c> answers "what
/// have I paid", and until this there was no row-by-row answer to "what do I owe" -- only
/// a per-group balance with nothing behind it. The rows have been there since splits
/// became their own table; these tests are about reading them, and about the one figure
/// that is easy to get wrong: a share of an expense you paid for yourself is not a debt.
/// </remarks>
public class TransactionShareTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> GroupWith(params Data.Entities.User[] others)
    {
        var group = await GetService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Home" }, Ct);

        if (others.Length > 0)
            await JoinGroup(group.Id, others);

        return group.Id;
    }

    private Task<Data.Entities.Expense> Expense(
        Guid? groupId, string name, decimal amount, Guid? paidBy = null,
        IReadOnlyList<SplitInput>? splits = null, DateTimeOffset? when = null) =>
        GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = name,
            Amount = amount,
            DateTime = when ?? DateTimeOffset.UtcNow,
            GroupId = groupId,
            PaidByUserId = paidBy,
            Splits = splits
        }, Ct).AsTask();

    private async Task<PagedResponse<ExpenseShareResponse>> Shares(
        TransactionFilter? filter = null, SortRequest? sort = null, PageRequest? page = null,
        bool owedOnly = false)
    {
        var transactions = GetService<ITransactionService>();
        var me = GetService<ICurrentUser>().User.Id;

        return await (await transactions.Shares(Ct)).ToSharePageAsync(
            await transactions.List(Ct),
            filter ?? new TransactionFilter(),
            sort ?? new SortRequest(),
            page ?? new PageRequest(),
            me, owedOnly, Ct);
    }

    private async Task<ExpenseShareSummaryResponse> Summary(TransactionFilter? filter = null,
        bool owedOnly = false)
    {
        var transactions = GetService<ITransactionService>();
        var me = GetService<ICurrentUser>().User.Id;

        return await (await transactions.Shares(Ct)).ToShareSummaryAsync(
            await transactions.List(Ct), filter ?? new TransactionFilter(), me, owedOnly, Ct);
    }

    [Fact]
    public async Task Owing_nothing_is_an_empty_page_rather_than_a_failure()
    {
        var page = await Shares();
        var summary = await Summary();

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, summary.Count);
        Assert.Equal(0m, summary.Total);
        Assert.Equal(0m, summary.Share);
        Assert.Equal(0m, summary.OwedToOthers);
    }

    /// <summary>
    /// The listing's reason for existing: an expense somebody else paid, which the listing
    /// beside it -- rows where the caller is the payer -- cannot show at all.
    /// </summary>
    [Fact]
    public async Task An_expense_somebody_else_paid_is_a_row_with_your_share_on_it()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        await Expense(home, "Dinner", 90m, paidBy: other.Id);

        var page = await Shares();

        var row = Assert.Single(page.Items);
        Assert.Equal("Dinner", row.Name);
        // Two numbers, and only one of them is theirs. A row carrying either alone would
        // be describing something nobody asked about.
        Assert.Equal(90m, row.Amount);
        Assert.Equal(45m, row.Share);
        Assert.False(row.PaidByYou);
        Assert.Equal(other.Id, row.PaidByUserId);
        Assert.Equal("Home", row.GroupName);
    }

    [Fact]
    public async Task An_expense_you_are_left_out_of_is_not_in_it_at_all()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        // "Don't charge me for his own birthday cake": a stated split naming only him.
        await Expense(home, "Cake", 40m, paidBy: other.Id,
            splits: [new SplitInput { UserId = other.Id, Amount = 40m }]);

        Assert.Empty((await Shares()).Items);
        Assert.Equal(0, (await Summary()).Count);
    }

    /// <summary>
    /// The figure the whole thing turns on. Your share of an expense you paid for is money
    /// you already have -- you are owed the rest of it -- so it belongs in the listing and
    /// not in what you owe.
    /// </summary>
    [Fact]
    public async Task Your_share_of_an_expense_you_paid_for_is_listed_but_is_not_a_debt()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        await Expense(home, "Dinner", 90m, paidBy: other.Id);
        await Expense(home, "Taxi", 30m);
        await Expense(groupId: null, "Coffee", 4m);

        var page = await Shares();
        var summary = await Summary();

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, summary.Count);
        Assert.Equal(124m, summary.Total);
        // 45 of the dinner, 15 of the taxi, all 4 of the coffee.
        Assert.Equal(64m, summary.Share);
        // Only the dinner. Counting the other two would put a personal coffee and a taxi
        // the caller paid for on the same side of the ledger as money they owe.
        Assert.Equal(45m, summary.OwedToOthers);

        var mine = page.Items.Single(row => row.Name == "Taxi");
        Assert.True(mine.PaidByYou);
    }

    /// <summary>
    /// The same expense reached from both sides adds up to the position the home page
    /// already shows, rather than to twice it.
    /// </summary>
    [Fact]
    public async Task An_expense_you_paid_and_owe_a_share_of_is_not_counted_against_you_twice()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        await Expense(home, "Groceries", 100m);

        var summary = await Summary();
        var position = await GetService<IGroupService>().GetPosition(Ct);

        // Half of it is the caller's own share, and they paid the whole thing, so they are
        // owed the other half and owe nothing.
        Assert.Equal(50m, summary.Share);
        Assert.Equal(0m, summary.OwedToOthers);
        Assert.Equal(50m, position.OwedToYou);
        Assert.Equal(0m, position.YouOwe);
    }

    [Fact]
    public async Task A_settlement_is_not_an_expense_and_so_is_not_a_share_of_one()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        await Expense(home, "Dinner", 90m, paidBy: other.Id);

        await GetService<IGroupService>().Settle(home, new SettleRequest
        {
            UserId = other.Id,
            Amount = 45m,
            Direction = SettlementDirection.YouPaidThem
        }, Ct);

        // The transfer carries a split to the recipient, so a listing reading the split
        // table without the join would show a repayment as something still owed.
        var page = await Shares();

        Assert.Equal("Dinner", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Only_your_own_share_of_an_expense_is_yours_to_read()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        await Expense(home, "Dinner", 90m);

        var page = await Shares();

        // One row, not two: the caller's own share of it, never the other member's.
        var row = Assert.Single(page.Items);
        Assert.Equal(45m, row.Share);
    }

    [Fact]
    public async Task A_filter_narrows_it_the_way_it_narrows_the_expense_listing()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);
        var trip = await GroupWith(other);

        await Expense(home, "Dinner", 90m, paidBy: other.Id);
        await Expense(trip, "Hotel", 300m, paidBy: other.Id);

        var page = await Shares(new TransactionFilter(GroupId: trip));
        var summary = await Summary(new TransactionFilter(GroupId: trip));

        Assert.Equal("Hotel", Assert.Single(page.Items).Name);
        Assert.Equal(1, summary.Count);
        Assert.Equal(150m, summary.Share);
    }

    [Fact]
    public async Task Search_reaches_the_expense_behind_the_share()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        await Expense(home, "Dinner", 90m, paidBy: other.Id);
        await Expense(home, "Taxi", 30m, paidBy: other.Id);

        var page = await Shares(new TransactionFilter(Search: "din"));

        Assert.Equal("Dinner", Assert.Single(page.Items).Name);
    }

    /// <summary>
    /// The one key this listing has that the expense listing does not, and the one people
    /// actually want: which of these is costing me the most.
    /// </summary>
    [Fact]
    public async Task It_sorts_by_your_share_rather_than_by_the_whole_expense()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        var me = GetService<ICurrentUser>().User.Id;

        // The larger expense leaves the caller the smaller share, so the two orders differ.
        await Expense(home, "Hotel", 300m, paidBy: other.Id, splits:
        [
            new SplitInput { UserId = me, Amount = 10m },
            new SplitInput { UserId = other.Id, Amount = 290m }
        ]);

        await Expense(home, "Dinner", 90m, paidBy: other.Id);

        var byShare = await Shares(sort: new SortRequest("share"));
        var byAmount = await Shares(sort: new SortRequest("amount"));

        Assert.Equal(["Dinner", "Hotel"], byShare.Items.Select(row => row.Name));
        Assert.Equal(["Hotel", "Dinner"], byAmount.Items.Select(row => row.Name));
    }

    [Fact]
    public async Task A_sort_key_it_does_not_offer_is_refused_by_name()
    {
        var error = await Assert.ThrowsAsync<GroupSplit.API.Errors.ValidationException>(
            () => Shares(sort: new SortRequest("colour")));

        Assert.Contains("share", error.Message);
    }

    [Fact]
    public async Task The_page_reports_the_same_total_the_summary_counts()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);

        foreach (var index in Enumerable.Range(1, 7))
        {
            await Expense(home, $"Shop {index}", index * 10m, paidBy: other.Id,
                when: DateTimeOffset.UtcNow.AddDays(-index));
        }

        var page = await Shares(page: new PageRequest(Page: 1, PageSize: 3));
        var summary = await Summary();

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(7, page.TotalCount);
        Assert.Equal(page.TotalCount, summary.Count);
    }

    /// <summary>
    /// The rows are the ones every balance is summed from, so the listing and the group's
    /// own figures cannot disagree.
    /// </summary>
    [Fact]
    public async Task The_share_is_the_stored_split_and_not_a_second_division()
    {
        var other = await CreateNewUser();
        var home = await GroupWith(other);
        var me = GetService<ICurrentUser>().User.Id;

        // An odd cent: the payer absorbs the remainder, so an even division of 0.05 between
        // two people is not 0.025 each and cannot be re-derived by dividing.
        var expense = await Expense(home, "Odd", 0.05m, paidBy: other.Id);

        var stored = await DbContext.Set<Data.Entities.TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id && split.UserId == me)
            .Select(split => split.Amount)
            .FirstAsync(Ct);

        Assert.Equal(stored, Assert.Single((await Shares()).Items).Share);
    }
}

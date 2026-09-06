using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// The figures shown beside a page. They exist because a page cannot add itself up: the
/// tiles on the expenses screen say what every match comes to, and a client holding
/// twenty-five of two hundred rows can only total the twenty-five.
/// </summary>
public class TransactionSummaryTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private async Task<Guid> GroupWithRule(string name, string category)
    {
        var groups = GetService<IGroupService>();
        var rules = GetService<IRuleService>();
        var me = GetService<ICurrentUser>().User;

        var group = await groups.CreateGroup(new CreateGroupRequest { Name = name },
            TestContext.Current.CancellationToken);

        await CreateCategory(group.Id, category, new PercentSplitRuleDto { Percentages = new() { [me.Id] = 100m } });

        return group.Id;
    }

    private async Task Expense(Guid groupId, string name, decimal amount, DateTimeOffset when)
    {
        var groups = GetService<IGroupService>();

        var ruleVersionId = await (await groups.GetGroupById(groupId, TestContext.Current.CancellationToken))
            .SelectMany(g => g.Rules)
            .SelectMany(r => r.Versions)
            .Where(v => v.EndDateTime == null)
            .Select(v => v.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = name,
            Amount = amount,
            DateTime = when,
            GroupId = groupId,
            CategoryId = ruleVersionId
        }, TestContext.Current.CancellationToken);
    }

    private async Task<TransactionSummaryResponse> Summary(TransactionFilter filter)
    {
        var transactions = await GetService<ITransactionService>().List(TestContext.Current.CancellationToken);

        return await transactions.ToSummaryAsync(filter, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nothing_to_summarise_is_nothing_rather_than_a_failure()
    {
        var summary = await Summary(new TransactionFilter());

        Assert.Equal(0, summary.Count);
        Assert.Equal(0m, summary.Total);
    }

    [Fact]
    public async Task It_counts_and_totals_everything_the_filter_leaves()
    {
        var home = await GroupWithRule("Home", "Groceries");

        await Expense(home, "Costco", 20.50m, DateTimeOffset.UtcNow);
        await Expense(home, "Publix", 30.25m, DateTimeOffset.UtcNow);
        await Expense(home, "Aldi", 9.25m, DateTimeOffset.UtcNow);

        var summary = await Summary(new TransactionFilter());

        Assert.Equal(3, summary.Count);
        Assert.Equal(60m, summary.Total);
    }

    [Fact]
    public async Task A_filter_narrows_the_summary_the_same_way_it_narrows_the_listing()
    {
        var home = await GroupWithRule("Home", "Groceries");
        var trip = await GroupWithRule("Lisbon", "Lodging");

        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);
        await Expense(trip, "Hotel", 300m, DateTimeOffset.UtcNow);

        var summary = await Summary(new TransactionFilter(GroupId: trip));

        Assert.Equal(1, summary.Count);
        Assert.Equal(300m, summary.Total);
    }

    /// <summary>
    /// The two have to agree, because they are shown side by side: the count beside a page
    /// is the page's own total, and a client that saw them disagree would have no way to
    /// tell which one was lying.
    /// </summary>
    [Fact]
    public async Task The_count_is_the_same_number_the_page_reports_as_its_total()
    {
        var home = await GroupWithRule("Home", "Groceries");

        foreach (var i in Enumerable.Range(1, 7))
            await Expense(home, $"Shop {i}", i * 10m, DateTimeOffset.UtcNow.AddDays(-i));

        var filter = new TransactionFilter(Search: "shop");
        var transactions = await GetService<ITransactionService>().List(TestContext.Current.CancellationToken);

        var page = await transactions.ToTransactionPageAsync(
            filter, new SortRequest(), new PageRequest(Page: 1, PageSize: 3),
            TestContext.Current.CancellationToken);

        var summary = await Summary(filter);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(page.TotalCount, summary.Count);
        Assert.Equal(7, summary.Count);
    }
}

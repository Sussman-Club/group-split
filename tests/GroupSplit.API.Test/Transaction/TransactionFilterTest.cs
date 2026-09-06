using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// The filter behind both expense listings and their summaries. It runs in the database,
/// so what it can express is what a client can ask for -- the search box especially, which
/// used to be a client filtering the rows it had been handed and could therefore only ever
/// search the page in front of it.
/// </summary>
public class TransactionFilterTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
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

    private async Task<Guid> Expense(Guid groupId, string name, decimal amount,
        DateTimeOffset when, string? description = null)
    {
        var transactions = GetService<ITransactionService>();
        var groups = GetService<IGroupService>();

        var ruleVersionId = await (await groups.GetGroupById(groupId, TestContext.Current.CancellationToken))
            .SelectMany(g => g.Rules)
            .SelectMany(r => r.Versions)
            .Where(v => v.EndDateTime == null)
            .Select(v => v.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = name,
            Description = description,
            Amount = amount,
            DateTime = when,
            GroupId = groupId,
            CategoryId = ruleVersionId
        }, TestContext.Current.CancellationToken);

        return created.Id;
    }

    private async Task<List<TransactionResponse>> Filtered(TransactionFilter filter)
    {
        var transactions = await GetService<ITransactionService>().List(TestContext.Current.CancellationToken);

        return await transactions.ApplyFilter(filter).SelectDto()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_filter_at_all_narrows_nothing()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);
        await Expense(home, "Publix", 30m, DateTimeOffset.UtcNow);

        Assert.Equal(2, (await Filtered(new TransactionFilter())).Count);
    }

    [Fact]
    public async Task A_group_narrows_to_that_group()
    {
        var home = await GroupWithRule("Home", "Groceries");
        var trip = await GroupWithRule("Lisbon", "Lodging");

        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);
        await Expense(trip, "Hotel", 300m, DateTimeOffset.UtcNow);

        var matches = await Filtered(new TransactionFilter(GroupId: trip));

        Assert.Equal("Hotel", Assert.Single(matches).Name);
    }

    [Fact]
    public async Task A_payer_narrows_to_what_they_paid()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);

        var me = GetService<ICurrentUser>().User;

        Assert.Single(await Filtered(new TransactionFilter(PaidByUserId: me.Id)));
        Assert.Empty(await Filtered(new TransactionFilter(PaidByUserId: Guid.NewGuid())));
    }

    [Fact]
    public async Task A_category_is_matched_whatever_its_casing()
    {
        var home = await GroupWithRule("Home", "Groceries");
        var trip = await GroupWithRule("Lisbon", "Lodging");

        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);
        await Expense(trip, "Hotel", 300m, DateTimeOffset.UtcNow);

        var matches = await Filtered(new TransactionFilter(Category: "gRoCeRiEs"));

        Assert.Equal("Costco", Assert.Single(matches).Name);
    }

    [Fact]
    public async Task A_category_matches_the_whole_thing_and_not_a_part_of_it()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);

        Assert.Empty(await Filtered(new TransactionFilter(Category: "Groc")));
    }

    [Fact]
    public async Task A_date_range_takes_both_of_its_ends()
    {
        var home = await GroupWithRule("Home", "Groceries");
        var march = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

        await Expense(home, "Before", 10m, march.AddDays(-10));
        await Expense(home, "On the day", 20m, march);
        await Expense(home, "After", 30m, march.AddDays(10));

        var matches = await Filtered(new TransactionFilter(From: march, To: march));

        Assert.Equal("On the day", Assert.Single(matches).Name);
    }

    /// <summary>
    /// A client sends the offset its own clock is on. Npgsql writes a DateTimeOffset to a
    /// timestamptz column only at offset zero, so the filter has to normalise before the
    /// value ever reaches a parameter.
    /// </summary>
    [Fact]
    public async Task A_range_given_at_some_other_offset_still_means_the_same_moment()
    {
        var home = await GroupWithRule("Home", "Groceries");
        var noonUtc = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

        await Expense(home, "Lunch", 20m, noonUtc);

        // 14:00+02:00 is noon UTC: the same instant, said differently.
        var sameMomentElsewhere = new DateTimeOffset(2026, 3, 15, 14, 0, 0, TimeSpan.FromHours(2));

        var matches = await Filtered(new TransactionFilter(From: sameMomentElsewhere, To: sameMomentElsewhere));

        Assert.Equal("Lunch", Assert.Single(matches).Name);
    }

    [Theory]
    [InlineData("costco", "the name")]
    [InlineData("weekly shop", "the description")]
    [InlineData("grocer", "the category")]
    [InlineData("hom", "the group's name")]
    public async Task Search_matches_anywhere_it_is_asked_to(string term, string _)
    {
        var home = await GroupWithRule("Home", "Groceries");
        var trip = await GroupWithRule("Lisbon", "Lodging");

        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow, description: "The weekly shop");
        await Expense(trip, "Hotel", 300m, DateTimeOffset.UtcNow);

        var matches = await Filtered(new TransactionFilter(Search: term));

        Assert.Equal("Costco", Assert.Single(matches).Name);
    }

    [Fact]
    public async Task Search_matches_the_payer_by_name()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);

        var me = GetService<ICurrentUser>().User;
        me.FirstName = "Anabel";
        me.LastName = "Benitez";
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await Filtered(new TransactionFilter(Search: "anab")));
        Assert.Single(await Filtered(new TransactionFilter(Search: "benit")));
    }

    [Fact]
    public async Task Search_ignores_case_and_surrounding_space()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);

        Assert.Single(await Filtered(new TransactionFilter(Search: "  CoStCo  ")));
    }

    [Fact]
    public async Task A_blank_search_narrows_nothing()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);
        await Expense(home, "Publix", 30m, DateTimeOffset.UtcNow);

        Assert.Equal(2, (await Filtered(new TransactionFilter(Search: "   "))).Count);
    }

    [Fact]
    public async Task Search_that_matches_nothing_finds_nothing()
    {
        var home = await GroupWithRule("Home", "Groceries");
        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);

        Assert.Empty(await Filtered(new TransactionFilter(Search: "aldi")));
    }

    [Fact]
    public async Task Filters_narrow_together_rather_than_separately()
    {
        var home = await GroupWithRule("Home", "Groceries");
        var trip = await GroupWithRule("Lisbon", "Lodging");

        await Expense(home, "Costco", 20m, DateTimeOffset.UtcNow);
        await Expense(trip, "Costco", 40m, DateTimeOffset.UtcNow);

        var matches = await Filtered(new TransactionFilter(GroupId: home, Search: "costco"));

        Assert.Equal(20m, Assert.Single(matches).Amount);
    }
}

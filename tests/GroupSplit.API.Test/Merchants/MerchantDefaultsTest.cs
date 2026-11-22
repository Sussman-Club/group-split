using GroupSplit.API.Services;
using GroupSplit.API.Services.Banking.Suggestions;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GroupSplit.API.Test.Merchants;

/// <summary>
/// Where an expense at a merchant usually goes, for one typed in by hand (#548): the
/// caller's last expense there, and the classifier only when there is none.
/// </summary>
public class MerchantDefaultsTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private readonly Mock<IFilingSuggester> _suggester = new();

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddScoped(_ => _suggester.Object);

    [Fact]
    public async Task The_last_expense_at_a_merchant_gives_its_group_and_category()
    {
        var (group, _) = await GroupWithFriend();
        var food = await CreateEvenCategory(group, "Food");
        var groceries = await CreateEvenCategory(group, "Groceries");
        var mercadona = await Place("Mercadona");

        await Expense(group, mercadona, food, at: Day(1));
        var latest = await Expense(group, mercadona, groceries, at: Day(3));
        await Expense(group, await Place("Lidl"), food, at: Day(5));

        var usual = await Defaults(mercadona);

        Assert.Equal(latest.Id, usual.FromExpenseId);
        Assert.Equal(group, usual.Destination!.GroupId);
        Assert.Equal(groceries, usual.Destination.CategoryId);
        Assert.Equal("Groceries", usual.Destination.CategoryName);
        Assert.False(usual.Destination.IsPersonal);
    }

    [Fact]
    public async Task An_archived_category_is_dropped_and_the_group_kept()
    {
        var (group, _) = await GroupWithFriend();
        var food = await CreateEvenCategory(group, "Food");
        var mercadona = await Place("Mercadona");
        await Expense(group, mercadona, food, at: Day(1));

        var category = await DbContext.Set<Category>().SingleAsync(candidate => candidate.Id == food, Ct);
        category.ArchivedAt = DateTimeOffset.UtcNow;
        await DbContext.SaveChangesAsync(Ct);

        var usual = await Defaults(mercadona);

        Assert.Equal(group, usual.Destination!.GroupId);
        Assert.Null(usual.Destination.CategoryId);
        Assert.Equal("Home", usual.Destination.Destination);
    }

    /// <summary>
    /// "All for me" with no category: the split was the choice, so it is the split that
    /// comes back.
    /// </summary>
    [Fact]
    public async Task A_rule_the_last_expense_chose_without_a_category_comes_back()
    {
        var (group, _) = await GroupWithFriend();
        var allForMe = (await DbContext.Set<SplitRule>().AsNoTracking()
                .Include(rule => rule.Versions)
                .Where(rule => rule.Group.Id == group && rule.BuiltIn)
                .ToListAsync(Ct))
            .Single(rule => rule.AllFor == Me.Id);
        var pharmacy = await Place("Farmacia");

        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            MerchantId = pharmacy.Id,
            SplitRuleId = allForMe.Id,
            Name = "Painkillers",
            Amount = 9,
            DateTime = Day(2)
        }, Ct);

        var usual = await Defaults(pharmacy);

        Assert.Null(usual.Destination!.CategoryId);
        Assert.Equal(allForMe.Id, usual.Destination.SplitRuleId);
    }

    [Fact]
    public async Task An_even_division_under_no_category_suggests_the_group_alone()
    {
        var (group, _) = await GroupWithFriend();
        var bakery = await Place("Bakery");
        await Expense(group, bakery, category: null, at: Day(2));

        var usual = await Defaults(bakery);

        Assert.Equal(group, usual.Destination!.GroupId);
        Assert.Null(usual.Destination.CategoryId);
        Assert.Null(usual.Destination.SplitRuleId);
    }

    /// <summary>
    /// A place the caller knows only from a receipt in their inbox: nothing filed there yet,
    /// so the classifier is asked.
    /// </summary>
    [Fact]
    public async Task With_no_history_the_classifier_is_asked()
    {
        var mercadona = await Place("Mercadona");
        DbContext.Add(new Receipt { OwnerUserId = Me.Id, MerchantId = mercadona.Id, Subtotal = 12, Total = 12 });
        await DbContext.SaveChangesAsync(Ct);

        var guess = new FilingDestinationSuggestion(Me.Id, "Personal", true, null, null, 0.9);
        _suggester
            .Setup(suggester => suggester.SuggestMerchant("Mercadona", 12, "EUR", It.IsAny<CancellationToken>()))
            .ReturnsAsync(guess);

        var usual = await GetService<IMerchantDefaults>().For(mercadona.Id, "EUR", 12, Ct);

        Assert.Null(usual.FromExpenseId);
        Assert.Equal(guess, usual.Destination);
    }

    // ---- setup -----------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private static DateTimeOffset Day(int day) => new(2026, 9, day, 12, 0, 0, TimeSpan.Zero);

    private Task<MerchantDefaultsResponse> Defaults(Merchant merchant) =>
        GetService<IMerchantDefaults>().For(merchant.Id, "USD", null, Ct);

    private async Task<(Guid Group, Data.Entities.User Friend)> GroupWithFriend()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Home" }, Ct);
        var friend = await CreateNewUser();
        await JoinGroup(group.Id, friend);
        return (group.Id, friend);
    }

    private async Task<Merchant> Place(string name)
    {
        var merchant = new Merchant
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            FirstSeenAt = DateTimeOffset.UtcNow
        };
        DbContext.Add(merchant);
        await DbContext.SaveChangesAsync(Ct);
        return merchant;
    }

    private async Task<Expense> Expense(Guid group, Merchant merchant, Guid? category, DateTimeOffset at) =>
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            MerchantId = merchant.Id,
            CategoryId = category,
            Name = merchant.Name,
            Amount = 20,
            DateTime = at
        }, Ct);
}

using GroupSplit.API.Endpoints;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Group;

public class CategorySpendingTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task It_groups_expenses_by_category_and_leaves_out_settlements()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);
        var food = await CreateEvenCategory(group.Id, "Food");
        var archived = await CreateEvenCategory(group.Id, "Archived");
        var friend = await CreateNewUser();
        await JoinGroup(group.Id, friend);

        await Expense(group.Id, 30, "Dinner", food);
        await Expense(group.Id, 20, "Snacks", food);
        await Expense(group.Id, 10, "Cash", null);
        await Expense(group.Id, 8, "Old bill", archived);
        await GetService<ICategoryService>().SetArchived(archived, true, Ct);
        await GetService<ISettlementService>().Settle(group.Id,
            new SettleRequest { UserId = friend.Id, Amount = 25 }, Ct);

        var expenses = await GetService<ITransactionService>().InGroup(group.Id, Ct);
        var summary = await expenses.ToCategorySpendingAsync(new GroupSpendingFilter(), Me.Id, Ct);

        Assert.Equal("USD", summary.Currency);
        Assert.Equal(
            [new CategorySpendingResponse(food, "Food", 2, 50m, 25m),
             new CategorySpendingResponse(null, "Uncategorized", 1, 10m, 5m),
             new CategorySpendingResponse(archived, "Archived", 1, 8m, 4m)],
            summary.Categories);
    }

    [Fact]
    public async Task It_keeps_other_currencies_separate()
    {
        var category = await GetService<ICategoryService>().Create(new CreateCategoryRequest
        {
            GroupId = Me.Id, Name = "Travel"
        }, Ct);
        await Expense(Me.Id, 10, "USD", category.Id);
        await Expense(Me.Id, 20, "EUR", category.Id, "EUR");

        var expenses = await GetService<ITransactionService>().InGroup(Me.Id, Ct);
        var summary = await expenses.ToCategorySpendingAsync(new GroupSpendingFilter(), Me.Id, Ct);

        Assert.Equal("USD", summary.Currency);
        var eur = Assert.Single(summary.OtherCurrencies);
        Assert.Equal("EUR", eur.Currency);
        Assert.Equal(new CategorySpendingResponse(category.Id, "Travel", 1, 20m, 20m), Assert.Single(eur.Categories));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private Task Expense(Guid groupId, decimal amount, string name, Guid? categoryId, string currency = "USD") =>
        GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = groupId, PaidByUserId = Me.Id, CategoryId = categoryId, Name = name,
            Amount = amount, Currency = currency, DateTime = DateTimeOffset.UtcNow
        }, Ct).AsTask();
}

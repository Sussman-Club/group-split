using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// The personal group every account has: its id is the account's own, it is where an
/// expense of one's own goes, and nothing that needs a second person can be done to it.
/// </summary>
public class PersonalGroupTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private ITransactionService Transactions => GetService<ITransactionService>();
    private IGroupService Groups => GetService<IGroupService>();
    private Guid Me => GetService<ICurrentUser>().User.Id;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Expense> APersonalExpense(decimal amount = 30m, Guid? categoryId = null) =>
        Transactions.Create(new CreateTransactionRequest
        {
            Name = "Groceries",
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow,
            CategoryId = categoryId
        }, Ct).AsTask();

    private static async Task RefusedAsPersonal(Func<Task> act)
    {
        var refusal = await Assert.ThrowsAsync<ConflictException>(act);
        Assert.Equal(ErrorCodes.PersonalGroupNotShared, refusal.Code);
    }

    [Fact]
    public async Task An_expense_with_no_group_named_goes_in_the_payers_personal_group()
    {
        var expense = await APersonalExpense();

        Assert.Equal(Me, expense.GroupId);

        var share = Assert.Single(await DbContext.Set<TransactionSplit>()
            .Where(split => split.TransactionId == expense.Id).ToListAsync(Ct));
        Assert.Equal(Me, share.UserId);
        Assert.Equal(30m, share.Amount);
    }

    /// <summary>
    /// Named by its id it is the same group: the id is the caller's, and the web app picks
    /// it from the list like any other group.
    /// </summary>
    [Fact]
    public async Task Naming_the_personal_group_by_its_id_is_the_same_as_naming_none()
    {
        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Train",
            Amount = 12m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = Me,
            Currency = "EUR"
        }, Ct);

        Assert.Equal(Me, expense.GroupId);
        // Any currency, as a personal expense always could be.
        Assert.Equal("EUR", expense.Currency);
    }

    [Fact]
    public async Task A_personal_expense_can_be_filed_under_a_category_of_the_personal_group()
    {
        var category = await GetService<ICategoryService>().Create(new CreateCategoryRequest
        {
            GroupId = Me,
            Name = "Groceries"
        }, Ct);

        var expense = await APersonalExpense(categoryId: category.Id);

        Assert.Equal(category.Id, expense.CategoryId);
    }

    [Fact]
    public async Task A_personal_expense_cannot_be_split()
    {
        var other = await CreateNewUser();

        var refusal = await Assert.ThrowsAsync<ValidationException>(() => Transactions.Create(
            new CreateTransactionRequest
            {
                Name = "Dinner",
                Amount = 20m,
                DateTime = DateTimeOffset.UtcNow,
                Splits = [new SplitInput { UserId = Me, Amount = 10m }, new SplitInput { UserId = other.Id, Amount = 10m }]
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.SplitOnAPersonalExpense, refusal.Code);
    }

    /// <summary>
    /// Its balance is always zero and there is nobody to square up with, so the views that
    /// add groups up leave it out rather than show a row of noise.
    /// </summary>
    [Fact]
    public async Task The_personal_group_is_in_no_balance_that_spans_groups()
    {
        await APersonalExpense(50m);

        var position = await Groups.GetPosition(Ct);
        Assert.DoesNotContain(position.Groups, group => group.GroupId == Me);
        Assert.Equal(0m, position.Net);

        var balances = await (await Groups.GetAllGroupNetBalances(Ct)).ToListAsync(Ct);
        Assert.DoesNotContain(balances, balance => balance.GroupId == Me);
    }

    [Fact]
    public async Task Nobody_can_be_added_to_it_as_a_guest()
    {
        await RefusedAsPersonal(() => GetService<IGuestService>()
            .Add(Me, new AddGuestsRequest { Names = ["Ana"] }, Ct));
    }

    [Fact]
    public async Task It_has_no_join_link()
    {
        await RefusedAsPersonal(() => GetService<IJoinLinkService>().Create(Me, Ct));
    }

    [Fact]
    public async Task Its_owner_cannot_be_removed_from_it()
    {
        var other = await CreateNewUser();

        await RefusedAsPersonal(() => Groups.RemoveGroupMember(Me, other.Id, Ct));
    }

    [Fact]
    public async Task It_cannot_be_left()
    {
        await RefusedAsPersonal(() => Groups.Leave(Me, Ct));
    }

    [Fact]
    public async Task It_cannot_be_archived()
    {
        await RefusedAsPersonal(() => Groups.Archive(Me, Ct));
    }

    [Fact]
    public async Task It_has_no_split_rules()
    {
        await RefusedAsPersonal(() => GetService<ISplitRuleService>().Create(new CreateSplitRuleRequest
        {
            GroupId = Me,
            Name = "Mine",
            Definition = new EvenSplitRuleDto()
        }, Ct));
    }

    [Fact]
    public async Task Its_categories_divide_by_no_rule()
    {
        await RefusedAsPersonal(() => GetService<ICategoryService>().Create(new CreateCategoryRequest
        {
            GroupId = Me,
            Name = "Rent",
            DefaultSplitRuleId = Guid.NewGuid()
        }, Ct));
    }

    [Fact]
    public async Task It_has_no_default_rule()
    {
        await RefusedAsPersonal(() => Groups.UpdateGroup(Me, new CreateGroupRequest
        {
            Name = "Personal",
            DefaultSplitRuleId = Guid.NewGuid()
        }, Ct).AsTask());
    }

    [Fact]
    public async Task Nothing_is_settled_in_it()
    {
        var other = await CreateNewUser();
        var settlements = GetService<ISettlementService>();

        await RefusedAsPersonal(() => settlements.Settle(Me, new SettleRequest { UserId = other.Id, Amount = 5m }, Ct));
        await RefusedAsPersonal(() => settlements.SettleUp(Me, new SettleUpRequest(), Ct));
        await RefusedAsPersonal(() => settlements.RecordRepayment(Me, new RecordRepaymentRequest
        {
            FromUserId = Me,
            ToUserId = other.Id,
            Amount = 5m
        }, Ct));
    }

    /// <summary>
    /// Deleting the account leaves the personal group where it is, with the records in it:
    /// it has no balance to block the deletion and nobody to leave it to.
    /// </summary>
    [Fact]
    public async Task Deleting_the_account_is_not_blocked_by_its_personal_group()
    {
        await APersonalExpense(80m);

        var outstanding = await GetService<IAccountService>().DeleteAccount(Me, Ct);

        Assert.Empty(outstanding);
    }
}

using GroupSplit.API.Endpoints;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Who recorded a transaction and how, which is not always who paid it (#549).
/// </summary>
/// <remarks>
/// A member can record an expense somebody else paid, or a repayment between two others,
/// and before this the row said nothing about it: a repayment written by a third member
/// read as though the two people it named had written it.
/// </remarks>
public class TransactionEnteredByTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task An_expense_paid_by_somebody_else_is_entered_by_whoever_recorded_it()
    {
        var (group, friend, _) = await GroupOfThree();

        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            PaidByUserId = friend.Id,
            Name = "Dinner",
            Amount = 90,
            DateTime = DateTimeOffset.UtcNow
        }, Ct);

        Assert.Equal(friend.Id, expense.UserId);
        Assert.Equal(Me.Id, expense.EnteredById);
        Assert.Equal(TransactionSource.Manual, expense.Source);

        var details = await GetService<ITransactionService>().GetDetails(expense.Id, Ct);
        Assert.Equal(Me.Id, details!.EnteredByUserId);
        Assert.Equal("Ana Pérez", details.EnteredByUserName);
        Assert.Equal(TransactionSource.Manual, details.Source);

        var listed = await (await GetService<ITransactionService>().Get(expense.Id, Ct)).SelectDto().SingleAsync(Ct);
        Assert.Equal(Me.Id, listed.EnteredByUserId);
        Assert.Equal("Ana Pérez", listed.EnteredByUserName);
    }

    /// <summary>
    /// The case the issue was opened for: neither of the two people a repayment names wrote
    /// it, and the ledger now says who did.
    /// </summary>
    [Fact]
    public async Task A_repayment_between_two_other_members_is_entered_by_whoever_recorded_it()
    {
        var (group, friend, other) = await GroupOfThree();

        await GetService<ISettlementService>().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = friend.Id,
            ToUserId = other.Id,
            Amount = 40
        }, Ct);

        var entry = Assert.Single(await Ledger(group));
        Assert.Equal(friend.Id, entry.PaidByUserId);
        Assert.Equal(other.Id, entry.PaidToUserId);
        Assert.Equal(Me.Id, entry.EnteredByUserId);
        Assert.Equal("Ana Pérez", entry.EnteredByUserName);
        Assert.Equal(TransactionSource.Manual, entry.Source);
    }

    [Fact]
    public async Task Settling_up_is_entered_by_whoever_settled()
    {
        var (group, friend, _) = await GroupOfThree();
        await Expense(group, 30, paidBy: friend);

        await GetService<ISettlementService>().SettleUp(group, new SettleUpRequest(), Ct);

        var transfer = await DbContext.Set<Data.Entities.Transfer>().AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Me.Id, transfer.UserId);
        Assert.Equal(Me.Id, transfer.EnteredById);
        Assert.Equal(TransactionSource.Manual, transfer.Source);
    }

    [Fact]
    public async Task Group_activity_narrows_to_who_entered_it_and_how()
    {
        var (group, friend, other) = await GroupOfThree();

        await Expense(group, 10, paidBy: friend, name: "Mine");
        await GetService<ISettlementService>().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = friend.Id,
            ToUserId = other.Id,
            Amount = 5
        }, Ct);
        await Entered(group, by: friend, TransactionSource.Bank, "Friend's bank row");

        Assert.Equal(["Friend's bank row"], Names(await Ledger(group, new ActivityFilter(EnteredBy: friend.Id))));
        Assert.Equal(2, (await Ledger(group, new ActivityFilter(EnteredBy: Me.Id))).Count);
        Assert.Equal(["Friend's bank row"], Names(await Ledger(group, new ActivityFilter(Source: TransactionSource.Bank))));
        Assert.Equal(2, (await Ledger(group, new ActivityFilter(Source: TransactionSource.Manual))).Count);
        Assert.Empty(await Ledger(group, new ActivityFilter(EnteredBy: Me.Id, Source: TransactionSource.Bank)));
    }

    /// <summary>
    /// Whatever the path would have called it, a request that came through MCP says MCP:
    /// that it was an assistant is the one fact nothing else on the row keeps.
    /// </summary>
    [Fact]
    public async Task Through_MCP_every_path_records_MCP()
    {
        _throughMcp = true;
        var (group, friend, other) = await GroupOfThree();

        var expense = await Expense(group, 20, paidBy: friend);
        await GetService<ISettlementService>().RecordRepayment(group, new RecordRepaymentRequest
        {
            FromUserId = friend.Id,
            ToUserId = other.Id,
            Amount = 5
        }, Ct);

        Assert.Equal(TransactionSource.Mcp, expense.Source);
        Assert.All(await Ledger(group), entry =>
        {
            Assert.Equal(TransactionSource.Mcp, entry.Source);
            Assert.Equal(Me.Id, entry.EnteredByUserId);
        });
    }

    // ---- setup -----------------------------------------------------------------------

    private bool _throughMcp;

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddScoped<IEntryChannel>(_ => new McpWhenAsked(() => _throughMcp));

    private sealed class McpWhenAsked(Func<bool> throughMcp) : IEntryChannel
    {
        public TransactionSource SourceFor(TransactionSource source) =>
            throughMcp() ? TransactionSource.Mcp : source;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Data.Entities.User Me => GetService<ICurrentUser>().User;

    private static List<string> Names(IEnumerable<GroupLedgerEntryResponse> entries) =>
        [.. entries.Select(entry => entry.Name)];

    private async Task<(Guid Group, Data.Entities.User Friend, Data.Entities.User Other)> GroupOfThree()
    {
        var group = await GetService<IGroupService>().CreateGroup(new CreateGroupRequest { Name = "Home" }, Ct);
        var friend = await CreateNewUser();
        var other = await CreateNewUser();

        var me = await DbContext.Set<Data.Entities.User>().FirstAsync(user => user.Id == Me.Id, Ct);
        me.FirstName = "Ana";
        me.LastName = "Pérez";
        await DbContext.SaveChangesAsync(Ct);

        await JoinGroup(group.Id, friend, other);

        return (group.Id, friend, other);
    }

    private async Task<Data.Entities.Expense> Expense(Guid group, decimal amount, Data.Entities.User paidBy,
        string name = "Dinner") =>
        await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            GroupId = group,
            PaidByUserId = paidBy.Id,
            Name = name,
            Amount = amount,
            DateTime = DateTimeOffset.UtcNow
        }, Ct);

    /// <summary>
    /// A row somebody else recorded. Written directly: the services record whoever is
    /// signed in, and in this host that is always the caller.
    /// </summary>
    private async Task Entered(Guid group, Data.Entities.User by, TransactionSource source, string name)
    {
        var expense = new Data.Entities.Expense
        {
            GroupId = group,
            UserId = by.Id,
            EnteredById = by.Id,
            Source = source,
            Name = name,
            Amount = 7,
            DateTime = DateTimeOffset.UtcNow
        };
        expense.Splits.Add(new Data.Entities.TransactionSplit { UserId = by.Id, Amount = 7 });
        DbContext.Add(expense);
        await DbContext.SaveChangesAsync(Ct);
    }

    private async Task<List<GroupLedgerEntryResponse>> Ledger(Guid group, ActivityFilter? filter = null)
    {
        var activity = await GetService<IGroupService>().GetGroupActivity(group, Ct);

        return await activity
            .ApplyFilter(filter)
            .ApplySort(new SortRequest(), GroupApi.ActivitySort)
            .SelectLedgerDto(Me.Id)
            .ToListAsync(Ct);
    }
}

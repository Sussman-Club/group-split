using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Splits as stored rows rather than as arithmetic re-run on every read.
/// </summary>
/// <remarks>
/// The invariant under all of it is that a transaction's splits sum to its amount. It has
/// no second line of defence: nothing else recomputes the division, so a split left behind
/// by an edit makes every balance in the group wrong and nothing says so.
/// </remarks>
public class StoredSplitTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private async Task<(Guid GroupId, Guid Self, Guid Other)> GroupOfTwo()
    {
        var groups = GetService<IGroupService>();
        var self = GetService<ICurrentUser>().User.Id;

        var group = await groups.CreateGroup(
            new CreateGroupRequest { Name = "Trip" }, TestContext.Current.CancellationToken);

        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        return (group.Id, self, other.Id);
    }

    private async Task<Guid> EvenCategory(Guid groupId, Guid a, Guid b) =>
        await CreateCategory(groupId, "Split", new PercentSplitRuleDto
            {
                Percentages = new Dictionary<Guid, decimal> { [a] = 50m, [b] = 50m }
            });

    private Task<List<TransactionSplit>> SplitsOf(Guid transactionId) =>
        DbContext.Set<TransactionSplit>()
            .Where(split => split.Transaction.Id == transactionId)
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Creating_an_expense_stores_a_split_for_every_participant()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await EvenCategory(groupId, self, other);

        var created = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId
        }, TestContext.Current.CancellationToken);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(2, splits.Count);
        Assert.Equal(100.00m, splits.Sum(split => split.Amount));
        Assert.All(splits, split => Assert.Equal(50.00m, split.Amount));
    }

    /// <summary>
    /// The regression test for a lost row. Splits carry client-generated ids, so a new one
    /// hanging off an already-tracked expense looks to EF like an existing row to update
    /// rather than a new one to insert -- which threw on save, and would have silently
    /// dropped the split had the key been generated any other way.
    /// </summary>
    [Fact]
    public async Task Editing_the_amount_replaces_the_splits_rather_than_adding_to_them()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await EvenCategory(groupId, self, other);
        var transactions = GetService<ITransactionService>();

        var created = await transactions.Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100.00m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            CategoryId = categoryId
        }, TestContext.Current.CancellationToken);

        await transactions.Update(created.Id, new UpdateTransactionRequest
        {
            Name = "Hotel",
            Amount = 50.00m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = self,
            CategoryId = categoryId
        }, TestContext.Current.CancellationToken);

        var splits = await SplitsOf(created.Id);

        Assert.Equal(2, splits.Count);
        Assert.Equal(50.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A personal expense has nobody to divide with, so the payer owes all of it and their
    /// net position does not move.
    /// </summary>
    [Fact]
    public async Task A_personal_expense_is_the_payers_alone()
    {
        var created = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Coffee",
            Amount = 4.50m,
            DateTime = DateTimeOffset.UtcNow
        }, TestContext.Current.CancellationToken);

        var split = Assert.Single(await SplitsOf(created.Id));

        Assert.Equal(4.50m, split.Amount);
        Assert.Equal(GetService<ICurrentUser>().User.Id, split.User.Id);
    }

    /// <summary>
    /// A settlement is a transfer, and a transfer is not an expense. It has to move the two
    /// balances without appearing in anything that means "what we spent".
    /// </summary>
    [Fact]
    public async Task A_settlement_moves_both_balances_and_appears_in_no_expense_list()
    {
        var (groupId, self, other) = await GroupOfTwo();

        await GetService<IGroupService>().Settle(groupId,
            new SettleRequest { UserId = other, Amount = 30.00m },
            TestContext.Current.CancellationToken);

        var expenses = await (await GetService<ITransactionService>().List(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(expenses);

        var balances = await (await GetService<IGroupService>()
                .GetGroupNetBalance(groupId, TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        // The caller is the creditor recording a repayment, so the debtor's position rises
        // by what they paid and the creditor's falls by it.
        Assert.Equal(30.00m, balances.Single(balance => balance.UserId == other).Balance);
        Assert.Equal(-30.00m, balances.Single(balance => balance.UserId == self).Balance);
    }

    /// <summary>
    /// Whatever else is true, a group's positions cancel out: every amount paid is owed by
    /// somebody. The old balance query could not promise this, because a split that
    /// truncated away a cent left it belonging to nobody.
    /// </summary>
    [Fact]
    public async Task A_groups_balances_sum_to_zero()
    {
        var (groupId, self, other) = await GroupOfTwo();
        var categoryId = await EvenCategory(groupId, self, other);
        var transactions = GetService<ITransactionService>();

        foreach (var amount in new[] { 10.01m, 0.01m, 33.33m })
        {
            await transactions.Create(new CreateTransactionRequest
            {
                Name = $"Item {amount}",
                Amount = amount,
                DateTime = DateTimeOffset.UtcNow,
                GroupId = groupId,
                CategoryId = categoryId
            }, TestContext.Current.CancellationToken);
        }

        await GetService<IGroupService>().Settle(groupId,
            new SettleRequest { UserId = other, Amount = 5.00m },
            TestContext.Current.CancellationToken);

        var balances = await (await GetService<IGroupService>()
                .GetGroupNetBalance(groupId, TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0m, balances.Sum(balance => balance.Balance));
    }
}

using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Tests for DELETE /transactions/{id} via ITransactionService.Delete
/// </summary>
public class TransactionDeleteTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task DeleteTransaction_RemovesTransactionForCurrentUser()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();

        // Ensure current user exists
        var user = userService.User;

        // Create a transaction inside the group
        var created = await transactionService.Create(
            new CreateTransactionRequest
            {
                Amount = 12.50m,
                PaidByUserId = user.Id,
                Name = "Coffee",
                DateTime = DateTime.UtcNow
            },
            TestContext.Current.CancellationToken);

        // Act
        await transactionService.Delete(created.Id, TestContext.Current.CancellationToken);

        // Assert — the transaction should no longer exist
        var result = await transactionService.Get(created.Id, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DeleteTransaction_NonExistentId_ThrowsException()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();

        // Ensure user exists

        var randomId = Guid.NewGuid();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            transactionService.Delete(randomId, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task DeleteTransaction_TransactionInGroupOfAnotherUser_ThrowsException()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();


        var otherTransaction = await TestDataUtils.CreateTransactionForNewUserAsync(ServiceProvider);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            transactionService.Delete(otherTransaction.Id, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);

        // Assert — transaction must still exist
        var stillExists = DbContext.Set<Data.Entities.Transaction>().Where(x => x.Id == otherTransaction.Id).ToList();
        Assert.NotEmpty(stillExists);
    }

    /// <summary>
    /// A settlement can be taken back.
    /// </summary>
    /// <remarks>
    /// It could not be, from anywhere, until this: the delete resolved the id through the
    /// expense listing, EF put the discriminator in the predicate, and a transfer answered
    /// "Transaction not found." -- so a repayment recorded in the wrong direction, or
    /// twice, went on moving balances until somebody opened the database. The row and its
    /// one split go together; the split is what the balances are read from.
    /// </remarks>
    [Fact]
    public async Task DeleteTransaction_Transfer_RemovesItAndItsSplit()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var groupService = GetService<IGroupService>();

        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Flat" },
            TestContext.Current.CancellationToken);

        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        await groupService.Settle(
            group.Id,
            new SettleRequest { UserId = other.Id, Amount = 20m },
            TestContext.Current.CancellationToken);

        var transfer = await DbContext.Set<Data.Entities.Transfer>()
            .FirstAsync(candidate => candidate.GroupId == group.Id,
                TestContext.Current.CancellationToken);

        // Act
        await transactionService.Delete(transfer.Id, TestContext.Current.CancellationToken);

        // Assert
        var remaining = await DbContext.Set<Data.Entities.Transaction>()
            .Where(candidate => candidate.Id == transfer.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(remaining);

        var splits = await DbContext.Set<Data.Entities.TransactionSplit>()
            .Where(split => split.TransactionId == transfer.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(splits);
    }

    /// <summary>
    /// Deleting the settlement puts the debt back.
    /// </summary>
    /// <remarks>
    /// The point of the feature rather than a restatement of the one above: a settlement
    /// means "this was paid", so removing one has to leave the balances reading exactly what
    /// they read before it was recorded. Asserted through the same balance query the app and
    /// the CLI show, not by counting rows -- rows can go and leave a total behind.
    /// </remarks>
    [Fact]
    public async Task DeleteTransaction_Transfer_PutsTheBalancesBack()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var groupService = GetService<IGroupService>();
        var currentUser = GetService<ICurrentUser>().User;

        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Balances" },
            TestContext.Current.CancellationToken);

        var other = await CreateNewUser();

        await JoinGroup(group.Id, other);

        await groupService.Settle(
            group.Id,
            new SettleRequest { UserId = other.Id, Amount = 30m },
            TestContext.Current.CancellationToken);

        // The settlement is on the books: the payer's net is up by it and the recipient's
        // is down, which is what a repayment does to a balance.
        var settled = await BalanceOf(groupService, group.Id, currentUser.Id);

        Assert.NotEqual(0m, settled);

        var transfer = await DbContext.Set<Data.Entities.Transfer>()
            .FirstAsync(candidate => candidate.GroupId == group.Id,
                TestContext.Current.CancellationToken);

        // Act
        await transactionService.Delete(transfer.Id, TestContext.Current.CancellationToken);

        // Assert — back to where it started, for both sides of it
        Assert.Equal(0m, await BalanceOf(groupService, group.Id, currentUser.Id));
        Assert.Equal(0m, await BalanceOf(groupService, group.Id, other.Id));
    }

    /// <summary>
    /// Widening the delete to transfers widened which kinds of row are in reach, and not
    /// whose rows: a settlement between two strangers, in a group the caller is not in, is
    /// still none of their business.
    /// </summary>
    [Fact]
    public async Task DeleteTransaction_TransferInAGroupTheCallerIsNotIn_ThrowsAndKeepsIt()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();

        var transfer = await TestDataUtils.CreateTransferForStrangersAsync(ServiceProvider);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            transactionService.Delete(transfer.Id, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);

        var stillExists = await DbContext.Set<Data.Entities.Transaction>()
            .Where(candidate => candidate.Id == transfer.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(stillExists);
    }

    private static async Task<decimal> BalanceOf(IGroupService groups, Guid groupId, Guid userId)
    {
        var balances = await groups.GetGroupNetBalance(groupId, TestContext.Current.CancellationToken);

        return await balances
            .Where(balance => balance.UserId == userId)
            .Select(balance => balance.Balance)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }
}

using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using TransactionEntity = GroupSplit.Data.Entities.Transaction;

namespace GroupSplit.API.Test.Transaction;

public class TransactionCreateTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task CreateTransaction_CreatesNewTransaction()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();
        var now = DateTimeOffset.UtcNow;

        var request = new CreateTransactionRequest
        {
            Name = "Lunch",
            Description = "Team lunch",
            Amount = 42.50m,
            DateTime = now,
            PaidByUserId = user.Id
        };

        // Act
        var result = await transactionService.Create(
            request,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);

        // Verify saved in DB
        var txnInDb = await DbContext.Set<TransactionEntity>()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Id == result.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(txnInDb);
        Assert.Equal("Lunch", txnInDb.Name);
        Assert.Equal("Team lunch", txnInDb.Description);
        Assert.Equal(42.50m, txnInDb.Amount);
        Assert.Equal(now, txnInDb.DateTime);
        Assert.Equal(user.Id, txnInDb.User.Id);
    }

    [Fact]
    public async Task CreateTransaction_MultipleCallsCreateSeparateTransactions()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();
        var now = DateTimeOffset.UtcNow;

        var request1 = new CreateTransactionRequest
        {
            Name = "Groceries",
            Amount = 20,
            DateTime = now,
            PaidByUserId = user.Id
        };

        var request2 = new CreateTransactionRequest
        {
            Name = "Taxi",
            Amount = 16,
            DateTime = now,
            PaidByUserId = user.Id
        };

        // Act
        var txn1 = await transactionService.Create(request1,
            TestContext.Current.CancellationToken);

        var txn2 = await transactionService.Create(request2,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(txn1.Id, txn2.Id);

        // Verify both exist in DB
        var txnCount = await DbContext.Set<TransactionEntity>()
            .CountAsync(t => t.Id == txn1.Id || t.Id == txn2.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, txnCount);
    }
}
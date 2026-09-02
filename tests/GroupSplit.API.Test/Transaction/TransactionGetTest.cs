using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Tests for GET /transactions/{id} via TransactionService.Get
/// </summary>
public class TransactionGetTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task GetTransactionById_ReturnsTransactionForCurrentUser()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();
        var now = DateTimeOffset.UtcNow;

        // Create a transaction for the current user
        var created = await transactionService.Create(new CreateTransactionRequest
        {
            Name = "Coffee",
            Amount = 5.25m,
            DateTime = now,
            PaidByUserId = user.Id
        }, TestContext.Current.CancellationToken);

        // Act
        var result = await transactionService.Get(created.Id, TestContext.Current.CancellationToken);
        var tx = await result.FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(tx);
        Assert.Equal(created.Id, tx.Id);
        Assert.Equal("Coffee", tx.Name);
        Assert.Equal(5.25m, tx.Amount);
    }

    [Fact]
    public async Task GetTransactionById_NonExistentId_ReturnsEmpty()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<IUserService>();

        await userService.GetCurrentUser();

        // Act
        var result = await transactionService.Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTransactionById_TransactionOfAnotherUser_ReturnsEmpty()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();

        var otherTransaction = await TestDataUtils.CreateTransactionForNewUserAsync(ServiceProvider);

        // Act
        var result = await transactionService.Get(otherTransaction.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }
}
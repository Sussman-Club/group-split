using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

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
}

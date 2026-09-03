using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Tests for listing transactions via TransactionService.List
/// </summary>
public class TransactionListTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task List_WhenUserHasNoTransactions_ReturnsEmpty()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();

        // Ensure user exists

        // Act
        var transactions = (await transactionService.List(TestContext.Current.CancellationToken)).ToList();

        // Assert
        Assert.Empty(transactions);
    }

    [Fact]
    public async Task List_ReturnsOnlyTransactionsForCurrentUser()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();

        var user = userService.User;
        var now = DateTimeOffset.UtcNow;

        var request1 = new CreateTransactionRequest
        {
            Name = "Coffee",
            Amount = 20,
            DateTime = now,
            PaidByUserId = user.Id
        };

        var transaction = await transactionService.Create(request1, TestContext.Current.CancellationToken);

        var otherTransaction = await TestDataUtils.CreateTransactionForNewUserAsync(ServiceProvider);

        // Act
        var result = (await transactionService.List(TestContext.Current.CancellationToken)).ToList();

        // Assert — only current user's transactions returned
        Assert.Single(result);
        Assert.Equal(transaction.Id, result[0].Id);
        Assert.DoesNotContain(result, x => x.Id == otherTransaction.Id);
    }
}

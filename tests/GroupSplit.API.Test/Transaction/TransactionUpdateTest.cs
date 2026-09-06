using GroupSplit.API.Services;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Transaction;

/// <summary>
/// Tests for UpdateTransactionRequest via ITransactionService.Update
/// </summary>
public class TransactionUpdateTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task UpdateTransaction_Successful()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();

        var currentUser = userService.User;

        // Create a transaction
        var transaction = await transactionService.Create(new CreateTransactionRequest
        {
            Name = "Old Transaction",
            Amount = 10,
            DateTime = DateTimeOffset.UtcNow
        }, TestContext.Current.CancellationToken);

        var updateRequest = new UpdateTransactionRequest
        {
            Name = "Updated Transaction",
            Description = "Updated Description",
            Amount = 20,
            DateTime = DateTimeOffset.UtcNow.AddHours(1),
            PaidByUserId = transaction.User.Id,
            CategoryId = transaction.CategoryId
        };

        // Act
        var updated =
            await transactionService.Update(transaction.Id, updateRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(transaction.Id, updated.Id);
        Assert.Equal(updateRequest.Name, updated.Name);
        Assert.Equal(updateRequest.Description, updated.Description);
        Assert.Equal(updateRequest.Amount, updated.Amount);
        Assert.Equal(updateRequest.DateTime, updated.DateTime);
        Assert.Equal(currentUser.Id, updated.User.Id);
    }

    [Fact]
    public async Task UpdateTransaction_TransactionNotFound_ThrowsException()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var request = new UpdateTransactionRequest
        {
            Name = "X",
            Amount = 1,
            DateTime = DateTimeOffset.UtcNow
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotFoundException>(async () =>
            await transactionService.Update(Guid.NewGuid(), request, TestContext.Current.CancellationToken));
        Assert.Equal("Transaction not found.", ex.Message);
    }

    [Fact]
    public async Task UpdateTransaction_CategoryNotFound_ThrowsException()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();
        var currentUser = userService.User;

        // A personal expense, filed under no category
        var transaction = await transactionService.Create(new CreateTransactionRequest
        {
            Name = "Old",
            Amount = 10,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = currentUser.Id
        }, TestContext.Current.CancellationToken);

        var request = new UpdateTransactionRequest
        {
            Name = "X",
            Amount = 5,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = currentUser.Id,
            CategoryId = Guid.NewGuid() // nonexistent
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotFoundException>(async () =>
            await transactionService.Update(transaction.Id, request, TestContext.Current.CancellationToken));
        Assert.Equal("Category not found.", ex.Message);
    }

    [Fact]
    public async Task UpdateTransaction_PaidByUserNotInGroup_ThrowsException()
    {
        // Arrange
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();

        var currentUser = userService.User;

        var transaction = await transactionService.Create(new CreateTransactionRequest
        {
            Name = "Old Tx",
            Amount = 10,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = currentUser.Id
        }, TestContext.Current.CancellationToken);

        var otherUser = await CreateNewUser();

        var request = new UpdateTransactionRequest
        {
            Name = "Update",
            Amount = 50,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = otherUser.Id, // shares no group with the current user
            CategoryId = transaction.CategoryId
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await transactionService.Update(transaction.Id, request, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.TransactionPayerNotInGroup, ex.Code);
    }
}
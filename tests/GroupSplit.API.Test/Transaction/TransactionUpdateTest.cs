using GroupSplit.API.Services;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    public async Task UpdateSettlement_Successful()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var settlementService = GetService<ISettlementService>();
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();
        var db = GetService<AppDbContext>();
        var currentUser = userService.User;

        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Flat" }, TestContext.Current.CancellationToken);
        var otherUser = await CreateNewUser();
        await JoinGroup(group.Id, otherUser);

        await settlementService.RecordRepayment(group.Id, new RecordRepaymentRequest
        {
            FromUserId = currentUser.Id,
            ToUserId = otherUser.Id,
            Amount = 30m,
            Description = "Cash"
        }, TestContext.Current.CancellationToken);

        var transfer = await db.Set<Transfer>().FirstAsync(t => t.GroupId == group.Id, TestContext.Current.CancellationToken);

        var updateRequest = new UpdateTransactionRequest
        {
            Name = "Settlement",
            Description = "Bank Transfer",
            Amount = 50m,
            DateTime = DateTimeOffset.UtcNow.AddHours(2),
            PaidByUserId = currentUser.Id,
            GroupId = group.Id,
            Splits = [new SplitInput { UserId = otherUser.Id, Amount = 50m }]
        };

        // Act
        var updated = await transactionService.Update(transfer.Id, updateRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(transfer.Id, updated.Id);
        Assert.Equal(50m, updated.Amount);
        Assert.Equal("Bank Transfer", updated.Description);

        var details = await transactionService.GetDetails(transfer.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(details);
        Assert.Equal(ActivityKind.Transfer, details.Kind);
        Assert.Equal(50m, details.Amount);
        Assert.Equal("Bank Transfer", details.Description);
        Assert.Equal(otherUser.Id, details.PaidToUserId);
    }

    [Fact]
    public async Task UpdateSettlement_WithSelf_ThrowsException()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var settlementService = GetService<ISettlementService>();
        var transactionService = GetService<ITransactionService>();
        var userService = GetService<ICurrentUser>();
        var db = GetService<AppDbContext>();
        var currentUser = userService.User;

        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Flat" }, TestContext.Current.CancellationToken);
        var otherUser = await CreateNewUser();
        await JoinGroup(group.Id, otherUser);

        await settlementService.RecordRepayment(group.Id, new RecordRepaymentRequest
        {
            FromUserId = currentUser.Id,
            ToUserId = otherUser.Id,
            Amount = 30m
        }, TestContext.Current.CancellationToken);

        var transfer = await db.Set<Transfer>().FirstAsync(t => t.GroupId == group.Id, TestContext.Current.CancellationToken);

        var updateRequest = new UpdateTransactionRequest
        {
            Name = "Settlement",
            Amount = 50m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = currentUser.Id,
            GroupId = group.Id,
            Splits = [new SplitInput { UserId = currentUser.Id, Amount = 50m }]
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await transactionService.Update(transfer.Id, updateRequest, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SettlementWithSelf, ex.Code);
    }
}
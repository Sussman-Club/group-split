using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using UserEntity = GroupSplit.Data.Entities.User;
using TransactionEntity = GroupSplit.Data.Entities.Transaction;
using GroupEntity = GroupSplit.Data.Entities.Group;

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
        var userService = GetService<IUserService>();

        // Ensure current user exists
        var user = await userService.GetCurrentUser();

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
        var userService = GetService<IUserService>();

        // Ensure user exists
        await userService.GetCurrentUser();

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
        var userService = GetService<IUserService>();

        await userService.GetCurrentUser();

        // Create "other" user
        var otherUser = new UserEntity
        {
            FirstName = "Other",
            Identity = new UserIdentity { IdentityId = Guid.NewGuid().ToString() },
            PersonalGroup = new GroupEntity
            {
                Name = "Personal",
                Rules =
                {
                    new Rule
                    {
                        Category = "Personal",
                        Versions =
                        {
                            new PersonalRuleVersion
                            {
                                StartDateTime = DateTime.UtcNow.AddDays(-2)
                            }
                        }
                    }
                }
            }
        };

        var otherTransaction = new TransactionEntity
        {
            Name = "Other User Tx",
            Amount = 10.00m,
            DateTime = DateTimeOffset.UtcNow,
            User = otherUser,
            RuleVersion = otherUser.PersonalGroup.Rules.First().Versions.First()
        };

        DbContext.Set<UserEntity>().Add(otherUser);
        DbContext.Set<TransactionEntity>().Add(otherTransaction);
        
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            transactionService.Delete(otherTransaction.Id, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotNull(exception);

        // Assert — transaction must still exist
        var stillExists = DbContext.Set<TransactionEntity>().Where(x => x.Id == otherTransaction.Id).ToList();
        Assert.NotEmpty(stillExists);
    }
}
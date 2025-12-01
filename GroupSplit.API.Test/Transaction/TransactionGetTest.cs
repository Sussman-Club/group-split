using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;
using UserEntity = GroupSplit.Data.Entities.User;
using TransactionEntity = GroupSplit.Data.Entities.Transaction;
using GroupEntity = GroupSplit.Data.Entities.Group;

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
        var userService = GetService<IUserService>();

        var currentUser = await userService.GetCurrentUser();
        var now = DateTimeOffset.UtcNow;

        // Create another user (not related to current user)
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

        // Persist other user and rule hierarchy
        DbContext.Set<UserEntity>().Add(otherUser);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Extract rule version explicitly
        var ruleVersion = otherUser.PersonalGroup
            .Rules.SelectMany(r => r.Versions)
            .First();

        // Create a transaction belonging exclusively to the other user
        var otherTx = new TransactionEntity
        {
            Name = "Other User Purchase",
            Amount = 12.50m,
            DateTime = now,
            User = otherUser,
            RuleVersion = ruleVersion
        };

        DbContext.Set<TransactionEntity>().Add(otherTx);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await transactionService.Get(otherTx.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }
}
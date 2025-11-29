using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using UserEntity = GroupSplit.Data.Entities.User;
using TransactionEntity = GroupSplit.Data.Entities.Transaction;
using GroupEntity = GroupSplit.Data.Entities.Group;

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
        var userService = GetService<IUserService>();

        // Ensure user exists
        await userService.GetCurrentUser();

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
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();
        var now = DateTimeOffset.UtcNow;

        var request1 = new CreateTransactionRequest
        {
            Name = "Coffee",
            Amount = 20,
            DateTime = now,
            PaidByUserId = user.Id
        };

        var t1 = await transactionService.Create(request1,
            TestContext.Current.CancellationToken);

        // Create other user + their transaction
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
                                StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2))
                            }
                        }
                    }
                }
            }
        };

        var t2 = new TransactionEntity
        {
            Name = "Other User Tx",
            Amount = 10.00m,
            DateTime = DateTimeOffset.UtcNow,
            User = otherUser,
            RuleVersion = otherUser.PersonalGroup.Rules.First().Versions.First()
        };

        DbContext.Set<UserEntity>().Add(otherUser);
        DbContext.Set<TransactionEntity>().Add(t2);

        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = (await transactionService.List(TestContext.Current.CancellationToken)).ToList();

        // Assert — only current user's transactions returned
        Assert.Single(result);
        Assert.Equal(t1.Id, result[0].Id);
        Assert.DoesNotContain(result, x => x.Id == t2.Id);
    }
}
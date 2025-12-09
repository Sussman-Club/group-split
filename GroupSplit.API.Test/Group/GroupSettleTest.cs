using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

public class GroupSettleTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Settle_CreatesTwoTransactions()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();

        var currentUser = await userService.GetCurrentUser();

        // Create a new group with current user
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Settle Group" },
            TestContext.Current.CancellationToken);

        // Add another member
        var otherUser = await CreateNewUser();

        await groupService.AddGroupMembers(
            group.Id,
            new AddMemberRequest(
                [new UserIdentifier { Email = otherUser.Email! }]
            ),
            TestContext.Current.CancellationToken);

        var request = new SettleRequest
        {
            UserId = otherUser.Id,
            Amount = 50
        };

        // Act
        await groupService.Settle(
            group.Id,
            request,
            TestContext.Current.CancellationToken);

        var transactions = await DbContext.Set<SettlementRuleVersion>()
            .SelectMany(x => x.Transactions)
            .Where(x => x.User == currentUser || x.User == otherUser)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, transactions.Count);

        var tForOther = transactions.First(t => t.User.Id == otherUser.Id);
        var tForCurrent = transactions.First(t => t.User.Id == currentUser.Id);

        Assert.Equal(50, tForOther.Amount);
        Assert.Equal(-50, tForCurrent.Amount);

        Assert.IsType<SettlementRuleVersion>(tForOther.RuleVersion);
        Assert.IsType<SettlementRuleVersion>(tForCurrent.RuleVersion);

        Assert.Equal(otherUser.Id, tForOther.User.Id);
        Assert.Equal(currentUser.Id, tForCurrent.User.Id);
    }

    [Fact]
    public async Task Settle_UserNotFoundInGroup_Throws()
    {
        // Arrange
        var groupService = GetService<IGroupService>();

        // Create group with only current user
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Settle Group" },
            TestContext.Current.CancellationToken);

        var request = new SettleRequest
        {
            UserId = Guid.NewGuid(), // Not in the group
            Amount = 100
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            groupService.Settle(group.Id, request,
                TestContext.Current.CancellationToken));

        Assert.Contains("User was not found", ex.Message);
    }

    [Fact]
    public async Task Settle_GroupNotFound_Throws()
    {
        // Arrange
        var groupService = GetService<IGroupService>();

        var request = new SettleRequest
        {
            UserId = Guid.NewGuid(),
            Amount = 10
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            groupService.Settle(Guid.NewGuid(), request,
                TestContext.Current.CancellationToken));

        Assert.Contains("Group was not found", ex.Message);
    }
}
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Rules;

public class RulesDeleteTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Delete_SetsEndDateTime_OnExistingPersonalRuleVersion()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        // Create group
        var createGroupRequest = new CreateGroupRequest { Name = "Group Delete Test" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        // Create rule
        var createRuleRequest = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        };

        var createdVersion = await rulesService.Create(createRuleRequest, TestContext.Current.CancellationToken);

        // Act
        await rulesService.Delete(createdVersion.Rule.Id, TestContext.Current.CancellationToken);

        // Assert
        var updatedVersion = await DbContext.Set<RuleVersion>()
            .FirstAsync(v => v.Id == createdVersion.Id,
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(updatedVersion.EndDateTime);
        Assert.True(updatedVersion.EndDateTime <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Delete_SetsEndDateTime_OnExistingPercentRuleVersion()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<ICurrentUser>();
        var groupService = GetService<IGroupService>();

        var user = userService.User;

        // Create group
        var createGroupRequest = new CreateGroupRequest { Name = "Percent Delete Test" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        // Add another user
        var otherUser = await CreateNewUser();
        await groupService.AddGroupMembers(group.Id,
            new AddMemberRequest([new UserIdentifier { Email = otherUser.Email }]),
            TestContext.Current.CancellationToken);

        var userA = user.Id;
        var userB = otherUser.Id;

        // Create percent rule
        var createRuleRequest = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Percent",
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal>
                {
                    [userA] = 60,
                    [userB] = 40
                }
            }
        };

        var createdVersion = await rulesService.Create(createRuleRequest, TestContext.Current.CancellationToken);

        // Act
        await rulesService.Delete(createdVersion.Rule.Id, TestContext.Current.CancellationToken);

        // Assert
        var updatedVersion = await DbContext.Set<PercentRuleVersion>()
            .Include(v => v.RuleUsers)
            .FirstAsync(v => v.Id == createdVersion.Id,
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(updatedVersion.EndDateTime);
        Assert.True(updatedVersion.EndDateTime <= DateTime.UtcNow);

        // Ensure RuleUsers are still intact
        Assert.Equal(2, updatedVersion.RuleUsers.Count);
        Assert.Contains(updatedVersion.RuleUsers, ru => ru.User.Id == userA);
        Assert.Contains(updatedVersion.RuleUsers, ru => ru.User.Id == userB);
    }

    [Fact]
    public async Task Delete_Throws_WhenRuleDoesNotExist()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var nonExistentRuleId = Guid.NewGuid();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rulesService.Delete(nonExistentRuleId, TestContext.Current.CancellationToken));

        Assert.Equal("Rule does not exist.", ex.Message);
    }

    [Fact]
    public async Task Delete_Throws_WhenCalledTwice()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var createGroupRequest = new CreateGroupRequest { Name = "Group Double Delete" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var createRuleRequest = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        };

        var createdVersion = await rulesService.Create(createRuleRequest, TestContext.Current.CancellationToken);

        // Act
        await rulesService.Delete(createdVersion.Rule.Id, TestContext.Current.CancellationToken);

        // Assert - second delete should throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rulesService.Delete(createdVersion.Rule.Id, TestContext.Current.CancellationToken));

        Assert.Equal("Rule does not exist.", ex.Message);
    }
}
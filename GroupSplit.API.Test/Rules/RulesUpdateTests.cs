using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Rules;

public class RulesUpdateTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Update_ChangesCategory_WhenValid()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "A" },
            TestContext.Current.CancellationToken);

        var ruleVersion = await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "NewCat",
            Version = new PersonalRuleVersionDto()
        };

        // Act
        var updated = await rulesService.Update(ruleVersion.Rule.Id, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("NewCat", ruleVersion.Rule.Category);
        Assert.Equal(ruleVersion.Id, updated.Id); // same version, no new version created
    }

    [Fact]
    public async Task Update_DoesNotCreateNewVersion_WhenVersionIsIdentical()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "A" },
            TestContext.Current.CancellationToken);

        var ruleVersion = await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "Personal", // unchanged
            Version = new PersonalRuleVersionDto() // identical
        };

        // Act
        var updated = await rulesService.Update(ruleVersion.Rule.Id, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(ruleVersion.Rule.Versions);
        Assert.Equal(ruleVersion.Id, updated.Id);
    }

    [Fact]
    public async Task Update_CreatesNewVersion_WhenPercentagesChange()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();
        var groupService = GetService<IGroupService>();

        var user = await userService.GetCurrentUser();

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "Group" },
            TestContext.Current.CancellationToken);

        // Add another user
        var otherUser = await CreateNewUser();
        await groupService.AddGroupMembers(group.Id,
            new AddMemberRequest([new UserIdentifier { Email = otherUser.Email }]),
            TestContext.Current.CancellationToken);

        // Create percent rule
        var createRuleRequest = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Percent",
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal>
                {
                    [user.Id] = 50,
                    [otherUser.Id] = 50
                }
            }
        };

        var createdVersion = await rulesService.Create(createRuleRequest, TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "Percent",
            Version = new PercentRuleVersionDto
            {
                Percentages =
                {
                    [user.Id] = 60,
                    [otherUser.Id] = 40
                }
            }
        };

        // Act
        var updated = await rulesService.Update(createdVersion.Rule.Id, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, createdVersion.Rule.Versions.Count); // new version created

        var newVersion = Assert.IsType<PercentRuleVersion>(updated);
        Assert.NotEqual(createdVersion.Id, newVersion.Id); // new version generated

        Assert.Equal(60, newVersion.RuleUsers.Single(u => u.User.Id == user.Id).Percentage);
        Assert.Equal(40, newVersion.RuleUsers.Single(u => u.User.Id == otherUser.Id).Percentage);
    }

    [Fact]
    public async Task Update_Throws_WhenRuleDoesNotExist()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();

        var request = new UpdateRuleRequest
        {
            Category = "Any",
            Version = new PersonalRuleVersionDto()
        };

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Update(Guid.NewGuid(), request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_Throws_WhenCategoryAlreadyExists()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "A" },
            TestContext.Current.CancellationToken);

        await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "A",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var ruleB = await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "B",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "A", // conflict
            Version = new PersonalRuleVersionDto()
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Update(ruleB.Rule.Id, request, TestContext.Current.CancellationToken));

        Assert.Equal("Group already has a rule with this category.", ex.Message);
    }

    [Fact]
    public async Task Update_Throws_WhenPercentVersionContainsNonExistingUser()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();
        var userService = GetService<IUserService>();

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "A" },
            TestContext.Current.CancellationToken);

        var rule = await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "A",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        var missingUser = Guid.NewGuid();

        var user = await userService.GetCurrentUser();
        
        var request = new UpdateRuleRequest
        {
            Category = "Percent",
            Version = new PercentRuleVersionDto
            {
                Percentages =
                {
                    [user.Id] = 50,
                    [missingUser] = 50
                }
            }
        };

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Update(rule.Rule.Id, request, TestContext.Current.CancellationToken));
    }
}
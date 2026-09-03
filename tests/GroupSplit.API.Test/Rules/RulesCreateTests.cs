using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Rules;

public class RulesCreateTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Create_CreatesPersonalRuleVersion_WhenUserBelongsToGroup()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var createGroupRequest = new CreateGroupRequest { Name = "Group A" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var request = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        };

        // Act
        var created = await rulesService.Create(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(created);
        Assert.Equal("Personal rule", created.Rule.Category);
        Assert.Single(created.Rule.Versions);

        Assert.IsType<PersonalRuleVersion>(created);
    }

    [Fact]
    public async Task Create_CreatesPercentRuleVersion_WhenUserBelongsToGroup()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<ICurrentUser>();
        var groupService = GetService<IGroupService>();

        var user = userService.User;

        var createGroupRequest = new CreateGroupRequest { Name = "Group B" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var otherUser = await CreateNewUser();

        await groupService.AddGroupMembers(group.Id,
            new AddMemberRequest([new UserIdentifier { Email = otherUser.Email }]),
            TestContext.Current.CancellationToken);

        var userA = user.Id;
        var userB = otherUser.Id;

        var request = new CreateRuleRequest
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

        // Act
        var created = await rulesService.Create(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(created);
        Assert.Equal("Percent", created.Rule.Category);
        Assert.Single(created.Rule.Versions);

        var percentVersion = Assert.IsType<PercentRuleVersion>(created);

        Assert.Equal(2, percentVersion.RuleUsers.Count);

        // Ensure both users are present
        Assert.Contains(percentVersion.RuleUsers, x => x.User.Id == userA);
        Assert.Contains(percentVersion.RuleUsers, x => x.User.Id == userB);

        // Retrieve them safely
        var userAEntry = percentVersion.RuleUsers.Single(x => x.User.Id == userA);
        var userBEntry = percentVersion.RuleUsers.Single(x => x.User.Id == userB);

        // Check values
        Assert.Equal(60, userAEntry.Percentage);
        Assert.Equal(40, userBEntry.Percentage);
        Assert.Equal(100, percentVersion.RuleUsers.Sum(x => x.Percentage));
    }

    [Fact]
    public async Task Create_Throws_WhenPercentageUserDoesNotExist()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<ICurrentUser>();
        var groupService = GetService<IGroupService>();

        var user = userService.User;

        var createGroupRequest = new CreateGroupRequest { Name = "Group X" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        // One real user (current user)
        var realUserId = user.Id;

        // One fake user ID that does NOT exist
        var missingUserId = Guid.NewGuid();

        var request = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Percent",
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal>
                {
                    [realUserId] = 70,
                    [missingUserId] = 30,
                }
            }
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await rulesService.Create(request, TestContext.Current.CancellationToken);
        });

        Assert.Equal("Some users in the percentage rule do not exist.", ex.Message);
    }

    [Fact]
    public async Task Create_Fails_WhenUserNotInGroup()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<ICurrentUser>();


        var group = new Data.Entities.Group
        {
            Name = "Forbidden"
        };

        DbContext.Add(group);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal",
            Version = new PersonalRuleVersionDto()
        };

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Create(request, TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task Create_Fails_WhenCategoryAlreadyExistsInGroup()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var createGroupRequest = new CreateGroupRequest { Name = "Group C" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var createRuleRequest1 = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        };

        await rulesService.Create(createRuleRequest1, TestContext.Current.CancellationToken);

        var createRuleRequest2 = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule", // duplicate
            Version = new PersonalRuleVersionDto()
        };

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Create(createRuleRequest2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_ReactivatesExpiredRuleByAddingNewVersion()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var createGroupRequest = new CreateGroupRequest { Name = "Group D" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var existingRule = await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        await rulesService.Delete(existingRule.Rule.Id, TestContext.Current.CancellationToken);

        var request = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        };

        // Act
        var newVersion = await rulesService.Create(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(newVersion);
        Assert.Equal("Personal rule", newVersion.Rule.Category);
        Assert.Equal(2, newVersion.Rule.Versions.Count); // existing + new
        Assert.Contains(newVersion, newVersion.Rule.Versions);
        Assert.All(newVersion.Rule.Versions, v => Assert.IsType<PersonalRuleVersion>(v));
    }
}

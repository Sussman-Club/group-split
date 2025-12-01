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
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "Group A"
        };

        DbContext.Add(group);
        user.Groups.Add(group);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal",
            Version = new PersonalRuleVersionDto()
        };

        // Act
        var created = await rulesService.Create(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(created);
        Assert.Equal("Personal", created.Rule.Category);
        Assert.Single(created.Rule.Versions);

        Assert.IsType<PersonalRuleVersion>(created);
    }

    [Fact]
    public async Task Create_CreatesPercentRuleVersion_WhenUserBelongsToGroup()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "Group B"
        };

        var otherUser = new Data.Entities.User
        {
            FirstName = "Other",
            Identity = new UserIdentity { IdentityId = Guid.NewGuid().ToString() },
            PersonalGroup = new GroupSplit.Data.Entities.Group
            {
                Name = "Personal"
            }
        };

        DbContext.Add(otherUser);
        DbContext.Add(group);
        user.Groups.Add(group);
        otherUser.Groups.Add(group);

        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        var userService = GetService<IUserService>();

        var currentUser = await userService.GetCurrentUser();

        // Create a valid group the current user belongs to
        var group = new GroupSplit.Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "Group With Missing User Test"
        };

        currentUser.Groups.Add(group);
        DbContext.Add(group);

        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // One real user (current user)
        var realUserId = currentUser.Id;

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
        var userService = GetService<IUserService>();

        await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
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
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "Group C"
        };

        user.Groups.Add(group);

        var existing = new Rule
        {
            Id = Guid.NewGuid(),
            Group = group,
            Category = "Personal"
        };

        DbContext.AddRange(group, existing);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal", // duplicate
            Version = new PersonalRuleVersionDto()
        };

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Create(request, TestContext.Current.CancellationToken));
    }
}
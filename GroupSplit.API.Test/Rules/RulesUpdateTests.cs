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
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "A"
        };
        user.Groups.Add(group);

        var version = new PersonalRuleVersion
        {
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = group,
            Category = "OldCat",
            Versions = { version }
        };

        DbContext.Add(rule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "NewCat",
            Version = new PersonalRuleVersionDto()
        };

        // Act
        var updated = await rulesService.Update(rule.Id, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("NewCat", rule.Category);
        Assert.Equal(version.Id, updated.Id); // same version, no new version created
    }

    [Fact]
    public async Task Update_DoesNotCreateNewVersion_WhenVersionIsIdentical()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "SameVer"
        };
        user.Groups.Add(group);

        var version = new PersonalRuleVersion
        {
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = group,
            Category = "Personal",
            Versions = { version }
        };

        DbContext.Add(rule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "Personal", // unchanged
            Version = new PersonalRuleVersionDto() // identical
        };

        // Act
        var updated = await rulesService.Update(rule.Id, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(rule.Versions);
        Assert.Equal(version.Id, updated.Id);
    }

    [Fact]
    public async Task Update_CreatesNewVersion_WhenPercentagesChange()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var otherUser = new Data.Entities.User
        {
            FirstName = "Other",
            Identity = new UserIdentity { IdentityId = Guid.NewGuid().ToString() },
            PersonalGroup = new Data.Entities.Group { Name = "Pg" }
        };

        DbContext.Add(otherUser);

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "Group A"
        };

        user.Groups.Add(group);
        otherUser.Groups.Add(group);

        var oldVersion = new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            RuleUsers =
            {
                new PercentRuleUser { User = user, Percentage = 50 },
                new PercentRuleUser { User = otherUser, Percentage = 50 }
            }
        };

        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            Category = "Percent",
            Group = group,
            Versions = { oldVersion }
        };

        DbContext.Add(rule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        var updated = await rulesService.Update(rule.Id, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, rule.Versions.Count); // new version created

        var newVersion = Assert.IsType<PercentRuleVersion>(updated);
        Assert.NotEqual(oldVersion.Id, newVersion.Id); // new version generated

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
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group { Name = "Group B" };
        user.Groups.Add(group);

        var ruleA = new Rule
        {
            Id = Guid.NewGuid(),
            Category = "A",
            Group = group,
            Versions = { new PersonalRuleVersion { StartDate = DateOnly.FromDateTime(DateTime.UtcNow) } }
        };

        var ruleB = new Rule
        {
            Id = Guid.NewGuid(),
            Category = "B",
            Group = group,
            Versions = { new PersonalRuleVersion { StartDate = DateOnly.FromDateTime(DateTime.UtcNow) } }
        };

        DbContext.AddRange(ruleA, ruleB);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var request = new UpdateRuleRequest
        {
            Category = "A", // conflict
            Version = new PersonalRuleVersionDto()
        };

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rulesService.Update(ruleB.Id, request, TestContext.Current.CancellationToken));

        Assert.Equal("Group already has a rule with this category.", ex.Message);
    }

    [Fact]
    public async Task Update_Throws_WhenPercentVersionContainsNonExistingUser()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group { Name = "Group C" };
        user.Groups.Add(group);

        var initialVersion = new PercentRuleVersion
        {
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            RuleUsers =
            {
                new PercentRuleUser { User = user, Percentage = 100 }
            }
        };

        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            Category = "Percent",
            Group = group,
            Versions = { initialVersion }
        };

        DbContext.Add(rule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var missingUser = Guid.NewGuid();

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
            rulesService.Update(rule.Id, request, TestContext.Current.CancellationToken));
    }
}
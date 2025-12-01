using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Rules;

public class RulesGetTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Get_ReturnsVersion_WhenUserHasAccess()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "Group1" };
        user.Groups.Add(group);

        var version = new PersonalRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
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

        // Act
        var query = await rulesService.Get(rule.Id, TestContext.Current.CancellationToken);
        var result = await query.SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(version.Id, result.Id);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenVersionDoesNotExist()
    {
        var rulesService = GetService<IRuleService>();

        var query = await rulesService.Get(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var result = await query.SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenVersionBelongsToAnotherGroup()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        // User group
        var userGroup = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "UserGroup" };
        user.Groups.Add(userGroup);

        // Foreign group (user NOT in this one)
        var foreignGroup = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "ForeignGroup" };

        var forbiddenVersion = new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        var forbiddenRule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = foreignGroup,
            Category = "Work",
            Versions = { forbiddenVersion }
        };

        DbContext.AddRange(userGroup, foreignGroup, forbiddenRule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var query = await rulesService.Get(forbiddenVersion.Id, TestContext.Current.CancellationToken);
        var result = await query.SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_ReturnsCorrectVersion_WhenMultipleGroupsExist()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();
        var user = await userService.GetCurrentUser();

        var g1 = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "G1" };
        var g2 = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "G2" };

        user.Groups.Add(g1); // User belongs ONLY to g1

        // Version the user CAN see
        var v1 = new PersonalRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        var rule1 = new Rule
        {
            Id = Guid.NewGuid(),
            Group = g1,
            Category = "Personal",
            Versions = { v1 }
        };

        // Version user CANNOT see
        var v2 = new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        };

        var rule2 = new Rule
        {
            Id = Guid.NewGuid(),
            Group = g2,
            Category = "Work",
            Versions = { v2 }
        };

        DbContext.AddRange(g1, g2, rule1, rule2);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var resultVisible =
            await (await rulesService.Get(rule1.Id, TestContext.Current.CancellationToken)).SingleOrDefaultAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        
        var resultHidden =
            await (await rulesService.Get(rule2.Id, TestContext.Current.CancellationToken)).SingleOrDefaultAsync(
                cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resultVisible);
        Assert.Equal(v1.Id, resultVisible.Id);

        Assert.Null(resultHidden);
    }
}
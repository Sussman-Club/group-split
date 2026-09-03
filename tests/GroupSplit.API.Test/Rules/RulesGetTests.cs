using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Rules;

public class RulesGetTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Get_ReturnsVersion_WhenUserHasAccess()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var group = await groupService.CreateGroup(new CreateGroupRequest { Name = "Group1" },
            TestContext.Current.CancellationToken);

        var version = await rulesService.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Percent",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        // Act
        var query = await rulesService.Get(version.Rule.Id, TestContext.Current.CancellationToken);
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

        // Foreign group (user NOT in this one)
        var foreignGroup = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "ForeignGroup" };
        var forbiddenRule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = foreignGroup,
            Category = "Work",
            Versions =
            {
                new PercentRuleVersion
                {
                    Id = Guid.NewGuid(),
                    StartDateTime = DateTime.UtcNow
                }
            }
        };

        DbContext.AddRange(foreignGroup, forbiddenRule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var query = await rulesService.Get(forbiddenRule.Id, TestContext.Current.CancellationToken);
        var result = await query.SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_ReturnsCorrectVersion_WhenMultipleGroupsExist()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<ICurrentUser>();
        var user = userService.User;

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
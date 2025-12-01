using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Rules;

public class RulesListTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task List_ReturnsVersions_ForUserGroups()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var g1 = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "A" };
        var g2 = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "B" };

        user.Groups.Add(g1);
        user.Groups.Add(g2);

        var rule1 = new Rule
        {
            Id = Guid.NewGuid(),
            Group = g1,
            Category = "Personal"
        };
        rule1.Versions.Add(new PersonalRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        });

        var rule2 = new Rule
        {
            Id = Guid.NewGuid(),
            Group = g2,
            Category = "Work"
        };
        rule2.Versions.Add(new PercentRuleVersion
        {
            Id = Guid.NewGuid(),
            StartDateTime = DateTime.UtcNow
        });

        DbContext.AddRange(g1, g2, rule1, rule2);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var listQuery = await rulesService.List(TestContext.Current.CancellationToken);
        var list = await listQuery.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task List_ReturnsEmpty_WhenUserHasNoGroups()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();
        user.Groups.Clear();

        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await rulesService.List(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task List_DoesNotReturnVersions_FromGroupsTheUserIsNotIn()
    {
        // Arrange
        var rulesService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        // A group the user IS in
        var userGroup = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "MyGroup" };
        user.Groups.Add(userGroup);

        // A group the user is NOT in
        var otherGroup = new Data.Entities.Group { Id = Guid.NewGuid(), Name = "OtherGroup" };

        // Rule belonging to the user's group
        var allowedRule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = userGroup,
            Category = "Personal",
            Versions =
            {
                new PersonalRuleVersion
                {
                    Id = Guid.NewGuid(),
                    StartDateTime = DateTime.UtcNow
                }
            }
        };

        // Rule belonging to the group the user is NOT in
        var forbiddenRule = new Rule
        {
            Id = Guid.NewGuid(),
            Group = otherGroup,
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

        DbContext.AddRange(userGroup, otherGroup, allowedRule, forbiddenRule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var listQuery = await rulesService.List(TestContext.Current.CancellationToken);
        var list = await listQuery.ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(list); // Only the allowed rule should appear
        Assert.Equal(allowedRule.Versions.First().Id, list.Single().Id);
    }
}
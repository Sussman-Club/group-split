using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
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
        var groupService = GetService<IGroupService>();

        var user = await userService.GetCurrentUser();

        var g1 = await groupService.CreateGroup(new CreateGroupRequest { Name = "A" },
            TestContext.Current.CancellationToken);
        var g2 = await groupService.CreateGroup(new CreateGroupRequest { Name = "B" },
            TestContext.Current.CancellationToken);

        await rulesService.Create(new CreateRuleRequest
        {
            GroupId = g1.Id,
            Category = "Personal",
            Version = new PersonalRuleVersionDto()
        }, TestContext.Current.CancellationToken);

        await rulesService.Create(new CreateRuleRequest
        {
            GroupId = g2.Id,
            Category = "Work",
            Version = new PercentRuleVersionDto
            {
                Percentages =
                {
                    [user.Id] = 100
                }
            }
        }, TestContext.Current.CancellationToken);

        // Act
        var listQuery = await rulesService.List(TestContext.Current.CancellationToken);
        var list = await listQuery.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, list.Count); // Including the personal rule version from the user's personal group
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
        Assert.Contains(list, x => x.Id == allowedRule.Versions.First().Id);
        Assert.DoesNotContain(list, x => x.Id == forbiddenRule.Versions.First().Id);
    }
}
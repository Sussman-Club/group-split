using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Rules;

public class RulesGetRuleDetailsTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task GetUpdateModel_ThrowsException_WhenRuleDoesNotExist()
    {
        // Arrange
        var ruleService = GetService<IRuleService>();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ruleService.GetRuleDetails(Guid.NewGuid(), TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task GetUpdateModel_ReturnsPersonalRule_WhenCreatedThroughService()
    {
        // Arrange
        var ruleService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var createGroupRequest = new CreateGroupRequest { Name = "Test Group" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var createRequest = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Personal rule",
            Version = new PersonalRuleVersionDto()
        };

        var createdVersion = await ruleService.Create(createRequest, TestContext.Current.CancellationToken);

        // Act
        var updateModel =
            await ruleService.GetRuleDetails(createdVersion.Rule.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(updateModel);
        Assert.Equal("Personal rule", updateModel.Category);
        Assert.IsType<PersonalRuleVersionDto>(updateModel.Version);
    }

    [Fact]
    public async Task GetUpdateModel_ReturnsPercentRule_WhenCreatedThroughService()
    {
        // Arrange
        var userService = GetService<IUserService>();
        var ruleService = GetService<IRuleService>();
        var groupService = GetService<IGroupService>();

        var currentUser = await userService.GetCurrentUser();
        var createGroupRequest = new CreateGroupRequest { Name = "Test Group" };
        var group = await groupService.CreateGroup(createGroupRequest, TestContext.Current.CancellationToken);

        var otherUser = new Data.Entities.User
        {
            FirstName = "OtherUser",
            Identity = new UserIdentity { IdentityId = Guid.NewGuid().ToString() },
            PersonalGroup = new Data.Entities.Group { Name = "Personal" }
        };

        otherUser.Groups.Add(group);
        DbContext.Add(otherUser);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var percentages = new Dictionary<Guid, decimal>
        {
            [currentUser.Id] = 75,
            [otherUser.Id] = 25
        };

        var createRequest = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Percent",
            Version = new PercentRuleVersionDto
            {
                Percentages = percentages
            }
        };

        var createdVersion = await ruleService.Create(createRequest, TestContext.Current.CancellationToken);

        // Act
        var updateModel =
            await ruleService.GetRuleDetails(createdVersion.Rule.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(updateModel);
        Assert.Equal("Percent", updateModel.Category);

        var dto = Assert.IsType<PercentRuleVersionDto>(updateModel.Version);

        Assert.Equal(2, dto.Percentages.Count);
        Assert.Equal(75, dto.Percentages[currentUser.Id]);
        Assert.Equal(25, dto.Percentages[otherUser.Id]);
    }

    [Fact]
    public async Task GetUpdateModel_AlwaysReturnsLatestVersion_WhenMultipleCreated()
    {
        // Arrange
        var ruleService = GetService<IRuleService>();
        var userService = GetService<IUserService>();

        var user = await userService.GetCurrentUser();

        var group = new Data.Entities.Group
        {
            Id = Guid.NewGuid(),
            Name = "Group Multi"
        };

        user.Groups.Add(group);
        DbContext.Add(group);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var create1 = new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Shared",
            Version = new PersonalRuleVersionDto()
        };

        var v1 = await ruleService.Create(create1, TestContext.Current.CancellationToken);

        var update = new UpdateRuleRequest
        {
            Version = new PercentRuleVersionDto
            {
                Percentages = new Dictionary<Guid, decimal>
                {
                    [user.Id] = 100
                }
            }
        };

        var v2 = await ruleService.Update(v1.Rule.Id, update, TestContext.Current.CancellationToken);

        // Act
        var updateModel = await ruleService.GetRuleDetails(v1.Rule.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(updateModel);
        Assert.IsType<PercentRuleVersionDto>(updateModel.Version); // latest takes precedence
    }
}
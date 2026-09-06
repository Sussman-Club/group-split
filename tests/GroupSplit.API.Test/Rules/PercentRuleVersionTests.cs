using GroupSplit.API.Services;
using GroupSplit.API.Errors;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Rules;

/// <summary>
/// The percent rule type, given the same treatment as
/// <see cref="SharesRuleVersionTests"/>: what it rejects, and when a saved edit counts as
/// the same rule rather than a new version of it. The comparison is what decides whether
/// history is kept, so getting it wrong either loses the old split or fills the rule with
/// duplicate versions.
/// </summary>
public class PercentRuleVersionTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private async Task<(Guid GroupId, Guid[] UserIds)> GroupOf(int others)
    {
        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Percent" }, TestContext.Current.CancellationToken);

        var ids = new List<Guid> { GetService<ICurrentUser>().User.Id };

        for (var i = 0; i < others; i++)
        {
            var member = await CreateNewUser();
            await groupService.AddGroupMembers(group.Id,
                new AddMemberRequest([new UserIdentifier { Email = member.Email! }]),
                TestContext.Current.CancellationToken);
            ids.Add(member.Id);
        }

        return (group.Id, [.. ids]);
    }

    private async Task<PercentRuleVersion> CreatePercent(
        Guid groupId, Dictionary<Guid, decimal> percentages)
    {
        var created = await GetService<IRuleService>().Create(new CreateRuleRequest
        {
            GroupId = groupId,
            Category = "Percent",
            Version = new PercentRuleVersionDto { Percentages = percentages }
        }, TestContext.Current.CancellationToken);

        return Assert.IsType<PercentRuleVersion>(created);
    }

    private Task Update(Guid ruleId, Dictionary<Guid, decimal> percentages) =>
        GetService<IRuleService>().Update(ruleId, new UpdateRuleRequest
        {
            Category = "Percent",
            Version = new PercentRuleVersionDto { Percentages = percentages }
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(0)]
    [InlineData(200)]
    public async Task Percentages_that_do_not_total_one_hundred_are_rejected(int firstShare)
    {
        var (groupId, users) = await GroupOf(1);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            CreatePercent(groupId, new Dictionary<Guid, decimal>
            {
                [users[0]] = firstShare,
                [users[1]] = 0
            }));

        Assert.Contains("sum to 100", exception.Message);
    }

    /// <summary>
    /// The total is checked against a small epsilon rather than exactly, so a split that
    /// cannot be written exactly in two places — three ways — is still accepted.
    /// </summary>
    [Fact]
    public async Task A_total_within_the_rounding_epsilon_is_accepted()
    {
        var (groupId, users) = await GroupOf(2);

        var version = await CreatePercent(groupId, new Dictionary<Guid, decimal>
        {
            [users[0]] = 33.33m,
            [users[1]] = 33.33m,
            [users[2]] = 33.34m
        });

        Assert.Equal(3, version.RuleUsers.Count);
    }

    [Fact]
    public async Task A_member_on_zero_percent_is_left_out_of_the_split()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreatePercent(groupId, new Dictionary<Guid, decimal>
        {
            [users[0]] = 100,
            [users[1]] = 0
        });

        Assert.Equal(users[0], Assert.Single(version.RuleUsers).User.Id);
    }

    [Fact]
    public async Task Percentages_for_someone_outside_the_group_are_rejected()
    {
        var (groupId, users) = await GroupOf(0);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            CreatePercent(groupId, new Dictionary<Guid, decimal>
            {
                [users[0]] = 50,
                [Guid.NewGuid()] = 50
            }));

        Assert.Contains("not members", exception.Message);
    }

    [Fact]
    public async Task A_percent_rule_reads_back_as_the_percentages_it_was_given()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreatePercent(groupId, new Dictionary<Guid, decimal>
        {
            [users[0]] = 40,
            [users[1]] = 60
        });

        var details = await GetService<IRuleService>()
            .GetRuleDetails(version.Rule.Id, TestContext.Current.CancellationToken);

        var dto = Assert.IsType<PercentRuleVersionDto>(details.Version);
        Assert.Equal(40m, dto.Percentages[users[0]]);
        Assert.Equal(60m, dto.Percentages[users[1]]);
    }

    [Fact]
    public async Task Saving_the_same_percentages_again_does_not_add_a_version()
    {
        var (groupId, users) = await GroupOf(1);
        var split = new Dictionary<Guid, decimal> { [users[0]] = 40, [users[1]] = 60 };

        var version = await CreatePercent(groupId, split);

        await Update(version.Rule.Id, new Dictionary<Guid, decimal>(split));

        Assert.Single(version.Rule.Versions);
    }

    [Fact]
    public async Task Changing_a_percentage_adds_a_version()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreatePercent(groupId,
            new Dictionary<Guid, decimal> { [users[0]] = 40, [users[1]] = 60 });

        await Update(version.Rule.Id,
            new Dictionary<Guid, decimal> { [users[0]] = 30, [users[1]] = 70 });

        Assert.Equal(2, version.Rule.Versions.Count);
    }

    [Fact]
    public async Task Dropping_a_member_from_the_split_adds_a_version()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreatePercent(groupId,
            new Dictionary<Guid, decimal> { [users[0]] = 40, [users[1]] = 60 });

        await Update(version.Rule.Id, new Dictionary<Guid, decimal> { [users[0]] = 100 });

        Assert.Equal(2, version.Rule.Versions.Count);
    }

    /// <summary>
    /// Same number of members and the same percentages, but not the same members. Counting
    /// alone would call this unchanged and quietly keep splitting the bill the old way.
    /// </summary>
    [Fact]
    public async Task Swapping_who_the_split_applies_to_adds_a_version()
    {
        var (groupId, users) = await GroupOf(2);

        var version = await CreatePercent(groupId,
            new Dictionary<Guid, decimal> { [users[0]] = 50, [users[1]] = 50 });

        await Update(version.Rule.Id,
            new Dictionary<Guid, decimal> { [users[0]] = 50, [users[2]] = 50 });

        Assert.Equal(2, version.Rule.Versions.Count);
    }
}

/// <summary>
/// A rule flagged as the app's own, which a member may not edit or delete.
/// </summary>
/// <remarks>
/// These flags used to have exactly one producer: the settlement pseudo-rule, which
/// settling wrote its transactions against and which therefore could not be rewritten
/// afterwards without changing what a settled balance meant. A settlement is a
/// <see cref="Transfer"/> now -- one row, divided by no rule at all -- so that guarantee is
/// structural and needs no flag to hold it up.
/// <para>
/// The flags themselves are still live code with no producer left, so the rule here is
/// built directly. They go, with <see cref="RuleFlags"/>, in the step that deletes the old
/// model; until then what still exists stays tested.
/// </para>
/// </remarks>
public class ProtectedRuleTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private async Task<Rule> ProtectedRule()
    {
        var group = await GetService<IGroupService>().CreateGroup(
            new CreateGroupRequest { Name = "Settle" }, TestContext.Current.CancellationToken);

        var rule = new Rule
        {
            Category = "Protected",
            Flags = RuleFlags.NonEditable | RuleFlags.NonDeletable | RuleFlags.NoUserTransactions,
            Group = await DbContext.Set<Data.Entities.Group>()
                .FirstAsync(g => g.Id == group.Id, TestContext.Current.CancellationToken)
        };

        // A rule is reached through its current version, so one without a version is not
        // found at all and would pass these tests for the wrong reason.
        rule.Versions.Add(new PersonalRuleVersion { StartDateTime = DateTimeOffset.UtcNow });

        DbContext.Add(rule);
        await DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return rule;
    }

    [Fact]
    public async Task A_protected_rule_cannot_be_edited()
    {
        var rule = await ProtectedRule();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            GetService<IRuleService>().Update(rule.Id, new UpdateRuleRequest
            {
                Category = "Renamed",
                Version = new PersonalRuleVersionDto()
            }, TestContext.Current.CancellationToken));

        Assert.Contains("not editable", exception.Message);
    }

    [Fact]
    public async Task A_protected_rule_cannot_be_deleted()
    {
        var rule = await ProtectedRule();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            GetService<IRuleService>().Delete(rule.Id, TestContext.Current.CancellationToken));

        Assert.Contains("not deletable", exception.Message);
    }
}

using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Test.Rules;

/// <summary>
/// Shares are the one rule type that computes something rather than storing what it was
/// given: the handler turns share counts into percentages, and has to make them total
/// exactly 100 despite rounding each one to two places. None of that was covered.
/// </summary>
public class SharesRuleVersionTests(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    /// <summary>
    /// A group containing the current user plus <paramref name="others"/> extra members,
    /// returned current user first.
    /// </summary>
    private async Task<(Guid GroupId, Guid[] UserIds)> GroupOf(int others)
    {
        var groupService = GetService<IGroupService>();
        var group = await groupService.CreateGroup(
            new CreateGroupRequest { Name = "Shares" }, TestContext.Current.CancellationToken);

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

    private async Task<SharesRuleVersion> CreateShares(
        Guid groupId, Dictionary<Guid, int> shares, string category = "Shares")
    {
        var created = await GetService<IRuleService>().Create(new CreateRuleRequest
        {
            GroupId = groupId,
            Category = category,
            Version = new SharesRuleVersionDto { Shares = shares }
        }, TestContext.Current.CancellationToken);

        return Assert.IsType<SharesRuleVersion>(created);
    }

    [Fact]
    public async Task Equal_shares_between_two_members_are_half_each()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 1, [users[1]] = 1 });

        Assert.Equal(50d, version.RuleUsers.Single(u => u.User.Id == users[0]).Percentage);
        Assert.Equal(50d, version.RuleUsers.Single(u => u.User.Id == users[1]).Percentage);
    }

    [Fact]
    public async Task Uneven_shares_become_proportional_percentages()
    {
        var (groupId, users) = await GroupOf(1);

        // 3:1 of four shares.
        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 3, [users[1]] = 1 });

        Assert.Equal(75d, version.RuleUsers.Single(u => u.User.Id == users[0]).Percentage);
        Assert.Equal(25d, version.RuleUsers.Single(u => u.User.Id == users[1]).Percentage);
    }

    /// <summary>
    /// The case the drift correction exists for. Three equal shares round to 33.33 each,
    /// which totals 99.99; the missing cent is added to the last member so the rule still
    /// splits the whole bill. Without the correction a transaction would silently lose it.
    /// </summary>
    [Fact]
    public async Task Rounding_drift_is_absorbed_so_the_percentages_total_one_hundred()
    {
        var (groupId, users) = await GroupOf(2);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 1, [users[1]] = 1, [users[2]] = 1 });

        Assert.Equal(100d, version.RuleUsers.Sum(u => u.Percentage), 10);

        // Two members carry the rounded-down share and one carries the remainder.
        var percentages = version.RuleUsers.Select(u => u.Percentage).OrderBy(p => p).ToArray();
        Assert.Equal(33.33d, percentages[0], 10);
        Assert.Equal(33.33d, percentages[1], 10);
        Assert.Equal(33.34d, percentages[2], 10);
    }

    [Fact]
    public async Task The_original_share_counts_are_kept_alongside_the_percentages()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 3, [users[1]] = 1 });

        Assert.Equal(3, version.SharedRuleUsers.Single(u => u.User.Id == users[0]).Shares);
        Assert.Equal(1, version.SharedRuleUsers.Single(u => u.User.Id == users[1]).Shares);
    }

    /// <summary>
    /// A member can be listed with no shares — the UI offers everyone in the group — and
    /// they are left out of the split rather than being given 0%.
    /// </summary>
    [Fact]
    public async Task A_member_with_no_shares_is_left_out_of_the_split()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 1, [users[1]] = 0 });

        Assert.Equal(100d, Assert.Single(version.RuleUsers).Percentage);
        Assert.Equal(users[0], version.RuleUsers.Single().User.Id);
    }

    [Fact]
    public async Task A_rule_where_nobody_has_shares_is_rejected()
    {
        var (groupId, users) = await GroupOf(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateShares(groupId, new Dictionary<Guid, int> { [users[0]] = 0, [users[1]] = 0 }));

        Assert.Contains("No users have shares", exception.Message);
    }

    [Fact]
    public async Task Shares_for_someone_outside_the_group_are_rejected()
    {
        var (groupId, users) = await GroupOf(0);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateShares(groupId,
                new Dictionary<Guid, int> { [users[0]] = 1, [Guid.NewGuid()] = 1 }));

        Assert.Contains("do not exist", exception.Message);
    }

    [Fact]
    public async Task A_shares_rule_reads_back_as_the_shares_it_was_given()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 2, [users[1]] = 5 });

        var details = await GetService<IRuleService>()
            .GetRuleDetails(version.Rule.Id, TestContext.Current.CancellationToken);

        var dto = Assert.IsType<SharesRuleVersionDto>(details.Version);
        Assert.Equal(2, dto.Shares[users[0]]);
        Assert.Equal(5, dto.Shares[users[1]]);
    }

    [Fact]
    public async Task Saving_the_same_shares_again_does_not_add_a_version()
    {
        var (groupId, users) = await GroupOf(1);
        var shares = new Dictionary<Guid, int> { [users[0]] = 2, [users[1]] = 1 };

        var version = await CreateShares(groupId, shares);

        await GetService<IRuleService>().Update(version.Rule.Id, new UpdateRuleRequest
        {
            Category = "Shares",
            Version = new SharesRuleVersionDto { Shares = new Dictionary<Guid, int>(shares) }
        }, TestContext.Current.CancellationToken);

        Assert.Single(version.Rule.Versions);
    }

    [Fact]
    public async Task Changing_a_share_count_adds_a_version()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 2, [users[1]] = 1 });

        await GetService<IRuleService>().Update(version.Rule.Id, new UpdateRuleRequest
        {
            Category = "Shares",
            Version = new SharesRuleVersionDto
            {
                Shares = new Dictionary<Guid, int> { [users[0]] = 3, [users[1]] = 1 }
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, version.Rule.Versions.Count);
    }

    /// <summary>
    /// Same members, same total, but one fewer of them listed: the count check is what
    /// catches it, before any percentage is computed.
    /// </summary>
    [Fact]
    public async Task Dropping_a_member_from_the_shares_adds_a_version()
    {
        var (groupId, users) = await GroupOf(1);

        var version = await CreateShares(groupId,
            new Dictionary<Guid, int> { [users[0]] = 1, [users[1]] = 1 });

        await GetService<IRuleService>().Update(version.Rule.Id, new UpdateRuleRequest
        {
            Category = "Shares",
            Version = new SharesRuleVersionDto
            {
                Shares = new Dictionary<Guid, int> { [users[0]] = 1 }
            }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, version.Rule.Versions.Count);
    }
}

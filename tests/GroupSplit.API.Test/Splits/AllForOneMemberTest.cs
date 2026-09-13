using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Endpoints;
using GroupSplit.API.Test.Base;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// The rule every group holds for every member: all of it is for them.
/// </summary>
/// <remarks>
/// Nobody writes one, which is the point -- "this one is not shared, it is Ana's" is the
/// commonest thing an expense has to say that its category cannot, and a group should not
/// have to invent a rule apiece and keep them in step with its membership first. What that
/// costs is everything asserted here: they appear on their own, and nothing may change what
/// one says, because an expense can name one without anybody having created it.
/// </remarks>
public class AllForOneMemberTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupService Groups => GetService<IGroupService>();

    private ISplitRuleService Rules => GetService<ISplitRuleService>();

    private IMemberSplitRules Provisioned => GetService<IMemberSplitRules>();

    private Guid Self => GetService<ICurrentUser>().User.Id;

    private Task<List<SplitRule>> RulesOf(Guid groupId) =>
        DbContext.Set<SplitRule>()
            .Include(rule => rule.Versions)
            .Where(rule => rule.Group.Id == groupId)
            .ToListAsync(Ct);

    /// <summary>
    /// The rule the group was given for one member, found the way everything finds one: by
    /// the division it stands for. Nothing on the rule says who it is for.
    /// </summary>
    private async Task<SplitRule?> RuleFor(Guid groupId, Guid userId) =>
        (await RulesOf(groupId)).FirstOrDefault(rule => rule.BuiltIn && rule.AllFor == userId);

    [Fact]
    public async Task Creating_a_group_gives_its_creator_a_rule_that_puts_the_whole_amount_on_them()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var rule = await RuleFor(group.Id, Self);

        Assert.NotNull(rule);
        Assert.True(rule.BuiltIn);

        var version = Assert.IsType<SoleSplitRuleVersion>(Assert.Single(rule.Versions));

        Assert.Equal(Self, version.UserId);
        Assert.Null(version.SupersededAt);
    }

    /// <summary>
    /// The other way in. Both go through <see cref="IGroupJoiner"/>, which is the whole
    /// reason that class exists -- a member joined by link is a member joined by invitation
    /// in every respect, including this.
    /// </summary>
    [Fact]
    public async Task Joining_a_group_gives_the_new_member_one_too()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var other = await CreateNewUser();

        var loaded = await DbContext.Set<Data.Entities.Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == group.Id, Ct);

        var tracked = await DbContext.Set<Data.Entities.User>().FirstAsync(user => user.Id == other.Id, Ct);

        await GetService<IGroupJoiner>().Join(loaded, tracked, Ct);

        var rule = await RuleFor(group.Id, other.Id);

        Assert.NotNull(rule);
        Assert.Equal(other.Id, Assert.IsType<SoleSplitRuleVersion>(rule.Versions.Single()).UserId);
    }

    /// <summary>
    /// Find-or-create, so no way into a group has to know which ways came before: somebody
    /// who joined by link and then claimed an invitation to the same group passes through
    /// twice.
    /// </summary>
    [Fact]
    public async Task Provisioning_the_same_member_twice_writes_one_rule()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var loaded = await DbContext.Set<Data.Entities.Group>().FirstAsync(candidate => candidate.Id == group.Id, Ct);
        var me = await DbContext.Set<Data.Entities.User>().FirstAsync(user => user.Id == Self, Ct);

        await Provisioned.EnsureFor(loaded, me, Ct);

        Assert.Single((await RulesOf(group.Id)).Where(rule => rule.BuiltIn && rule.AllFor == Self));
    }

    /// <summary>
    /// Names are unique within a group, and nothing stops a group writing a rule called what
    /// the next member's would be called. A join is not a thing to refuse over.
    /// </summary>
    [Fact]
    public async Task A_name_the_group_has_already_used_is_worked_around_rather_than_refused()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var mine = await RuleFor(group.Id, Self);
        Assert.NotNull(mine);

        var other = await CreateNewUser();

        var loaded = await DbContext.Set<Data.Entities.Group>().FirstAsync(candidate => candidate.Id == group.Id, Ct);
        var tracked = await DbContext.Set<Data.Entities.User>().FirstAsync(user => user.Id == other.Id, Ct);

        // Named, so what the rule would be called is something this test can state rather
        // than derive: a test account has no profile and is listed by its address.
        tracked.FirstName = "Ana";
        tracked.LastName = null;
        await DbContext.SaveChangesAsync(Ct);

        // The name theirs would be provisioned under, written by hand first.
        await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "All for Ana",
            Definition = new SoleSplitRuleDto(Self)
        }, Ct);

        var theirs = await Provisioned.EnsureFor(loaded, tracked, Ct);

        Assert.Equal(other.Id, theirs.AllFor);
        Assert.NotEqual(mine.Name, theirs.Name);
        Assert.Equal("All for Ana (2)", theirs.Name);
    }

    [Fact]
    public async Task It_cannot_be_restated()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var rule = await RuleFor(group.Id, Self);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Rules.Update(
            rule!.Id,
            new UpdateSplitRuleRequest { Name = rule.Name, Definition = new EvenSplitRuleDto() },
            Ct));

        Assert.Equal(ErrorCodes.SplitRuleNotEditable, refusal.Code);
    }

    /// <summary>
    /// Renaming included. It goes through the same endpoint, and the reason to refuse is the
    /// same one: nothing about one of these is the group's to move.
    /// </summary>
    [Fact]
    public async Task It_cannot_be_renamed()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var rule = await RuleFor(group.Id, Self);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Rules.Update(
            rule!.Id,
            new UpdateSplitRuleRequest { Name = "Mine", Definition = new SoleSplitRuleDto(Self) },
            Ct));

        Assert.Equal(ErrorCodes.SplitRuleNotEditable, refusal.Code);
    }

    [Fact]
    public async Task It_cannot_be_deleted()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var rule = await RuleFor(group.Id, Self);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Rules.Delete(rule!.Id, Ct));

        Assert.Equal(ErrorCodes.SplitRuleNotEditable, refusal.Code);
    }

    [Fact]
    public async Task It_cannot_be_given_a_history()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var rule = await RuleFor(group.Id, Self);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Rules.SetHistory(
            rule!.Id,
            [new SplitRuleVersionInput(DateTimeOffset.UtcNow.AddYears(-1), new SoleSplitRuleDto(Self))],
            Ct));

        Assert.Equal(ErrorCodes.SplitRuleNotEditable, refusal.Code);
    }

    /// <summary>
    /// A group wanting "all of it is for Ana, until we say otherwise" writes one of its own,
    /// and that one is an ordinary rule in every respect -- including that it can be changed.
    /// </summary>
    [Fact]
    public async Task A_rule_the_group_writes_for_itself_is_the_same_kind_and_is_editable()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var written = await Rules.Create(new CreateSplitRuleRequest
        {
            GroupId = group.Id,
            Name = "Ana's gym",
            Definition = new SoleSplitRuleDto(Self)
        }, Ct);

        Assert.False(written.BuiltIn);

        // Renamed and restated, both of which a provisioned rule refuses -- and restated
        // within its own kind, which is the only restatement any rule takes.
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var updated = await Rules.Update(
            written.Id,
            new UpdateSplitRuleRequest { Name = "The gym", Definition = new SoleSplitRuleDto(other.Id) },
            Ct);

        Assert.Equal("The gym", updated.Name);
        Assert.Equal(other.Id, updated.AllFor);
    }

    [Fact]
    public async Task A_rule_naming_somebody_outside_the_group_is_refused()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var stranger = await CreateNewUser();

        var refusal = await Assert.ThrowsAsync<ValidationException>(() => Rules.Create(
            new CreateSplitRuleRequest
            {
                GroupId = group.Id,
                Name = "All for a stranger",
                Definition = new SoleSplitRuleDto(stranger.Id)
            },
            Ct));

        Assert.Equal(ErrorCodes.RuleUsersNotInGroup, refusal.Code);
    }

    /// <summary>
    /// The listing carries both halves: whether the group was given the rule, which is how a
    /// client tells the ones it may offer an Edit button beside from the ones it may not, and
    /// who it is for, which is how the expense dialog finds Ana's without reading every rule
    /// in the group.
    /// </summary>
    [Fact]
    public async Task The_listing_says_which_member_each_provisioned_rule_is_for()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);

        var listed = Assert.Single(
            await (await Rules.List(group.Id, Ct)).SelectDto().ToListAsync(Ct));

        Assert.True(listed.BuiltIn);
        Assert.Equal(Self, listed.AllForUserId);

        // And the details answer the same question through the division itself, which is
        // where a rule says who it is for.
        var details = await Rules.GetDetails(listed.Id, Ct);

        Assert.True(details.BuiltIn);
        Assert.Equal(Self, Assert.IsType<SoleSplitRuleDto>(details.Definition).UserId);
    }
}

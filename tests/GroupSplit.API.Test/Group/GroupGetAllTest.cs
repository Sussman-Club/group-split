using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using GroupSplit.Data.PostgreSQL;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Tests for GET /groups endpoint via GroupService.GetAllGroups
/// </summary>
public class GroupGetAllTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    [Fact]
    public async Task Group_pagination_translates_to_limit_and_offset_in_PostgreSQL()
    {
        await using var context = new PostgreSqlAppDbContext(
            new DbContextOptionsBuilder<PostgreSqlAppDbContext>()
                .UseNpgsql("Host=localhost;Database=query_translation").Options);
        var service = new GroupService(GetService<ICurrentUser>(), context,
            GetService<IGroupParticipants>(), GetService<ISplitRuleRevisions>(),
            GetService<IMemberSplitRules>(), GetService<IBillSplitRule>(),
            GetService<IEvenSplitRule>(), GetService<IJoinLinkService>(), TimeProvider.System);

        var sql = service.Describe(await service.GetGroups(new PageRequest(2, 4), false,
            TestContext.Current.CancellationToken)).ToQueryString();
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Contains("ArchivedAt", sql);
    }

    [Fact]
    public async Task Group_pages_filter_archived_memberships_before_limiting_and_do_not_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = GetService<IGroupService>();
        foreach (var name in new[] { "A archived", "B trip", "C flat", "D team", "E lunch", "F holiday" })
            await service.CreateGroup(new CreateGroupRequest { Name = name }, ct);

        var all = await service.GetAllGroups(ct);
        var archived = await all.SingleAsync(g => g.Name == "A archived", ct);
        await service.Archive(archived.Id, ct);

        using var otherScope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(otherScope.ServiceProvider);
        await otherScope.ServiceProvider.GetRequiredService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "A somebody else's group" }, ct);

        var query = await service.GetGroups(new PageRequest(1, 4), false, ct);
        // The limit belongs to the provider query, before materialization/serialization.
        Assert.Contains("Take(4)", query.Expression.ToString());
        var preview = await service.Describe(query).ToListAsync(ct);
        Assert.Equal(4, preview.Count);
        Assert.True(preview[0].IsPersonal);
        Assert.Equal(new[] { "B trip", "C flat", "D team", "Personal" }, preview.Select(g => g.Name).Order());
        Assert.All(preview, g => Assert.False(g.IsArchive));
        Assert.Equal(7, await all.CountAsync(ct));

        var secondPage = await service.Describe(await service.GetGroups(new PageRequest(2, 4), false, ct)).ToListAsync(ct);
        Assert.Equal(new[] { "E lunch", "F holiday" }, secondPage.Select(g => g.Name));
        Assert.Empty(preview.Select(g => g.Id).Intersect(secondPage.Select(g => g.Id)));

        var archivedPage = await service.Describe(await service.GetGroups(new PageRequest(1, 4), true, ct)).ToListAsync(ct);
        Assert.Equal("A archived", Assert.Single(archivedPage).Name);

        // Existing consumers that omit paging still receive the complete list.
        Assert.Equal(7, await (await service.GetGroups(cancellationToken: ct)).CountAsync(ct));
    }

    /// <summary>
    /// A new account is in one group: its personal one, provisioned with it so that an
    /// expense of one's own has somewhere to go, and keyed on the account's own id.
    /// </summary>
    [Fact]
    public async Task GetAllGroups_WhenTheAccountIsNew_ReturnsOnlyThePersonalGroup()
    {
        var groupService = GetService<IGroupService>();
        var me = GetService<ICurrentUser>().User.Id;

        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        var personal = Assert.Single(groups);
        Assert.Equal(me, personal.Id);
        Assert.Equal("Personal", personal.Name);
    }

    [Fact]
    public async Task GetAllGroups_ReturnsExactlyTheGroupsThatWereMade()
    {
        var groupService = GetService<IGroupService>();

        await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group 1" },
            TestContext.Current.CancellationToken);
        await groupService.CreateGroup(new CreateGroupRequest { Name = "Test Group 2" },
            TestContext.Current.CancellationToken);

        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        // The two that were made, and the personal group every account has.
        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Name == "Test Group 1");
        Assert.Contains(groups, g => g.Name == "Test Group 2");
    }

    [Fact]
    public async Task GetAllGroups_OnlyReturnsGroupsForCurrentUser()
    {
        // Arrange
        var groupService = GetService<IGroupService>();
        await groupService.CreateGroup(new CreateGroupRequest { Name = "My Group" },
            TestContext.Current.CancellationToken);

        var otherScope = GetService<IServiceScopeFactory>().CreateScope();
        await InitializeCurrentUser(otherScope.ServiceProvider);
        await otherScope.ServiceProvider.GetRequiredService<IGroupService>()
            .CreateGroup(new CreateGroupRequest { Name = "Their Group" },
                TestContext.Current.CancellationToken);

        // Act
        var groups = (await groupService.GetAllGroups(TestContext.Current.CancellationToken)).ToList();

        // Assert: theirs is not here, and neither is their personal group.
        Assert.Equal(["My Group", "Personal"], groups.Select(g => g.Name).Order());

        otherScope.Dispose();
    }
}

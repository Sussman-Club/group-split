using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Archiving a group is like archiving a note: it moves out of your list and nothing else
/// happens. The group is not closed, the other members are not told, and every write it
/// would have taken it still takes -- from you as much as from anyone. These are mostly
/// about what archiving does <em>not</em> do, because that is the part easy to get wrong.
/// </summary>
public class GroupArchiveTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IGroupService Groups => GetService<IGroupService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Data.Entities.Group> AGroup(string name = "Lisbon") =>
        Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct).AsTask();

    private async Task<bool> IsArchivedFor(Guid groupId, Guid userId) =>
        await DbContext.Set<Data.Entities.GroupMembership>()
            .Where(membership => membership.GroupId == groupId && membership.UserId == userId)
            .Select(membership => membership.ArchivedAt != null)
            .FirstAsync(Ct);

    [Fact]
    public async Task Archiving_marks_it_for_the_person_who_archived_it()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(group.Id, Ct);

        Assert.True(await IsArchivedFor(group.Id, me.Id));
    }

    [Fact]
    public async Task Unarchiving_brings_it_back()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(group.Id, Ct);
        await Groups.Unarchive(group.Id, Ct);

        Assert.False(await IsArchivedFor(group.Id, me.Id));
    }

    [Fact]
    public async Task Archiving_twice_is_not_an_error_and_does_not_move_the_moment()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(group.Id, Ct);

        var first = await DbContext.Set<Data.Entities.GroupMembership>()
            .Where(m => m.GroupId == group.Id && m.UserId == me.Id)
            .Select(m => m.ArchivedAt)
            .FirstAsync(Ct);

        await Groups.Archive(group.Id, Ct);

        var second = await DbContext.Set<Data.Entities.GroupMembership>()
            .Where(m => m.GroupId == group.Id && m.UserId == me.Id)
            .Select(m => m.ArchivedAt)
            .FirstAsync(Ct);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Unarchiving_one_that_was_never_archived_is_not_an_error()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Unarchive(group.Id, Ct);

        Assert.False(await IsArchivedFor(group.Id, me.Id));
    }

    /// <summary>
    /// The whole point of putting the flag on the membership: one member tidying their own
    /// list must not tidy anybody else's.
    /// </summary>
    [Fact]
    public async Task Archiving_is_mine_alone_and_the_other_members_never_see_it()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        var other = await CreateNewUser();
        await Groups.AddGroupMembers(group.Id,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]), Ct);

        await Groups.Archive(group.Id, Ct);

        Assert.True(await IsArchivedFor(group.Id, me.Id));
        Assert.False(await IsArchivedFor(group.Id, other.Id));
    }

    /// <summary>
    /// An archived group is hidden, not closed. Every write it took before, it still takes
    /// -- including from the person who archived it, who may well open it to add the
    /// expense that made them want it back.
    /// </summary>
    [Fact]
    public async Task An_archived_group_still_takes_every_write_it_took_before()
    {
        var group = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(group.Id, Ct);

        // Renamed.
        await Groups.UpdateGroup(group.Id, new CreateGroupRequest { Name = "Lisbon 2026" }, Ct);

        // A rule added, and an expense recorded against it.
        var version = await GetService<IRuleService>().Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Lodging",
            Version = new PercentRuleVersionDto { Percentages = new() { [me.Id] = 100m } }
        }, Ct);

        var expense = await GetService<ITransactionService>().Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            RuleVersionId = version.Id
        }, Ct);

        // A member added, and one removed.
        var other = await CreateNewUser();
        await Groups.AddGroupMembers(group.Id,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]), Ct);
        await Groups.RemoveGroupMember(group.Id, other.Id, Ct);

        Assert.Equal("Lisbon 2026",
            (await (await Groups.GetGroupById(group.Id, Ct)).FirstAsync(Ct)).Name);
        Assert.NotEqual(Guid.Empty, expense.Id);

        // And it is still archived through all of that.
        Assert.True(await IsArchivedFor(group.Id, me.Id));
    }

    [Fact]
    public async Task An_archived_group_is_still_one_of_your_groups()
    {
        var group = await AGroup();

        await Groups.Archive(group.Id, Ct);

        var mine = await (await Groups.GetAllGroups(Ct)).ToListAsync(Ct);

        Assert.Contains(mine, g => g.Id == group.Id);
    }

    [Fact]
    public async Task A_group_that_is_not_yours_cannot_be_archived_and_does_not_say_it_exists()
    {
        Guid theirGroup;

        using (var scope = ServiceProvider.CreateScope())
        {
            await InitializeCurrentUser(scope.ServiceProvider);
            var theirs = await scope.ServiceProvider.GetRequiredService<IGroupService>()
                .CreateGroup(new CreateGroupRequest { Name = "Not mine" }, Ct);
            theirGroup = theirs.Id;
        }

        var failure = await Assert.ThrowsAsync<NotFoundException>(() => Groups.Archive(theirGroup, Ct));

        Assert.Equal(ErrorCodes.GroupNotFound, failure.Code);
    }
}

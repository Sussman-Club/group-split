using GroupSplit.API.Endpoints;
using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Archiving is how a group is put down without being lost: the trip is over, nobody
/// should be adding to it, but the balances and the history are still worth reading and
/// someone may want it back. So every write refuses and every read carries on, and these
/// go through each write in turn -- a guard that covers eight paths and misses the ninth
/// is worse than none, because the ninth is the one nobody thinks to check.
/// </summary>
public class GroupArchiveTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IGroupService Groups => GetService<IGroupService>();
    private IRuleService Rules => GetService<IRuleService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A group with a rule anyone can record against, and one expense already in it.</summary>
    private async Task<(Guid GroupId, Guid RuleId, Guid RuleVersionId, Guid TransactionId)> AGroup(
        string name = "Lisbon")
    {
        var me = GetService<ICurrentUser>().User;

        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = name }, Ct);

        var version = await Rules.Create(new CreateRuleRequest
        {
            GroupId = group.Id,
            Category = "Lodging",
            Version = new PercentRuleVersionDto { Percentages = new() { [me.Id] = 100m } }
        }, Ct);

        var transaction = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            RuleVersionId = version.Id
        }, Ct);

        return (group.Id, version.Rule.Id, version.Id, transaction.Id);
    }

    private static async Task<ConflictException> Refuses(Func<Task> write)
    {
        var failure = await Assert.ThrowsAsync<ConflictException>(write);

        Assert.Equal(ErrorCodes.GroupArchived, failure.Code);

        return failure;
    }

    // ---- the switch itself ------------------------------------------------------------------

    [Fact]
    public async Task Archiving_a_group_marks_it_and_shows_on_the_group()
    {
        var (groupId, _, _, _) = await AGroup();

        await Groups.Archive(groupId, Ct);

        var group = await (await Groups.GetGroupById(groupId, Ct)).FirstAsync(Ct);

        Assert.True(group.IsArchived);
        Assert.NotNull(group.ArchivedAt);
    }

    /// <summary>
    /// Two members reaching for the same switch: the second one's tap should not become an
    /// error about the first one's.
    /// </summary>
    [Fact]
    public async Task Archiving_a_group_that_is_already_archived_changes_nothing()
    {
        var (groupId, _, _, _) = await AGroup();

        await Groups.Archive(groupId, Ct);
        var first = await (await Groups.GetGroupById(groupId, Ct)).Select(g => g.ArchivedAt).FirstAsync(Ct);

        await Groups.Archive(groupId, Ct);
        var second = await (await Groups.GetGroupById(groupId, Ct)).Select(g => g.ArchivedAt).FirstAsync(Ct);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Unarchiving_puts_it_back()
    {
        var (groupId, _, _, _) = await AGroup();

        await Groups.Archive(groupId, Ct);
        await Groups.Unarchive(groupId, Ct);

        var group = await (await Groups.GetGroupById(groupId, Ct)).FirstAsync(Ct);

        Assert.False(group.IsArchived);
        Assert.Null(group.ArchivedAt);
    }

    [Fact]
    public async Task Unarchiving_one_that_was_never_archived_is_not_an_error()
    {
        var (groupId, _, _, _) = await AGroup();

        await Groups.Unarchive(groupId, Ct);

        Assert.False((await (await Groups.GetGroupById(groupId, Ct)).FirstAsync(Ct)).IsArchived);
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

    // ---- every write in turn ----------------------------------------------------------------

    [Fact]
    public async Task An_archived_group_cannot_be_renamed()
    {
        var (groupId, _, _, _) = await AGroup();
        await Groups.Archive(groupId, Ct);

        await Refuses(() => Groups.UpdateGroup(groupId, new CreateGroupRequest { Name = "New name" }, Ct).AsTask());
    }

    [Fact]
    public async Task An_archived_group_cannot_take_a_new_member()
    {
        var (groupId, _, _, _) = await AGroup();
        await Groups.Archive(groupId, Ct);

        await Refuses(() => Groups.AddGroupMembers(groupId,
            new AddMemberRequest([new UserIdentifier { Email = "someone@test.com" }]), Ct));
    }

    /// <summary>
    /// The member here has no balance, so an unarchived group would let them go. Archived
    /// is the reason they cannot, and it is the reason they hear.
    /// </summary>
    [Fact]
    public async Task An_archived_group_cannot_lose_a_member()
    {
        var (groupId, _, _, _) = await AGroup();

        var other = await CreateNewUser();
        await Groups.AddGroupMembers(groupId,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]), Ct);

        await Groups.Archive(groupId, Ct);

        await Refuses(() => Groups.RemoveGroupMember(groupId, other.Id, Ct));
    }

    [Fact]
    public async Task An_archived_group_cannot_be_settled_up_in()
    {
        var (groupId, _, _, _) = await AGroup();

        var other = await CreateNewUser();
        await Groups.AddGroupMembers(groupId,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]), Ct);

        await Groups.Archive(groupId, Ct);

        await Refuses(() => Groups.Settle(groupId, new SettleRequest { UserId = other.Id, Amount = 10m }, Ct));
    }

    [Fact]
    public async Task An_archived_group_cannot_gain_a_rule()
    {
        var (groupId, _, _, _) = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(groupId, Ct);

        await Refuses(() => Rules.Create(new CreateRuleRequest
        {
            GroupId = groupId,
            Category = "Food",
            Version = new PercentRuleVersionDto { Percentages = new() { [me.Id] = 100m } }
        }, Ct));
    }

    [Fact]
    public async Task A_rule_in_an_archived_group_cannot_be_changed()
    {
        var (groupId, ruleId, _, _) = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(groupId, Ct);

        await Refuses(() => Rules.Update(ruleId, new UpdateRuleRequest
        {
            Category = "Somewhere else",
            Version = new PercentRuleVersionDto { Percentages = new() { [me.Id] = 100m } }
        }, Ct));
    }

    [Fact]
    public async Task A_rule_in_an_archived_group_cannot_be_deleted()
    {
        var (groupId, ruleId, _, _) = await AGroup();
        await Groups.Archive(groupId, Ct);

        await Refuses(() => Rules.Delete(ruleId, Ct));
    }

    [Fact]
    public async Task An_archived_group_cannot_take_a_new_expense()
    {
        var (groupId, _, ruleVersionId, _) = await AGroup();
        await Groups.Archive(groupId, Ct);

        await Refuses(() => Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            RuleVersionId = ruleVersionId
        }, Ct).AsTask());
    }

    [Fact]
    public async Task An_expense_in_an_archived_group_cannot_be_edited()
    {
        var (groupId, _, ruleVersionId, transactionId) = await AGroup();
        var me = GetService<ICurrentUser>().User;

        await Groups.Archive(groupId, Ct);

        await Refuses(() => Transactions.Update(transactionId, new UpdateTransactionRequest
        {
            Name = "Hotel, corrected",
            Amount = 120m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = me.Id,
            RuleVersionId = ruleVersionId
        }, Ct).AsTask());
    }

    [Fact]
    public async Task An_expense_in_an_archived_group_cannot_be_deleted()
    {
        var (groupId, _, _, transactionId) = await AGroup();
        await Groups.Archive(groupId, Ct);

        await Refuses(() => Transactions.Delete(transactionId, Ct));
    }

    /// <summary>
    /// An edit can move an expense from one group to another. Neither end may be archived:
    /// leaving one would rewrite its balances, joining one would write into a closed ledger.
    /// </summary>
    [Fact]
    public async Task An_expense_cannot_be_moved_into_an_archived_group()
    {
        var live = await AGroup("Live");
        var archived = await AGroup("Archived");

        await Groups.Archive(archived.GroupId, Ct);

        var me = GetService<ICurrentUser>().User;

        await Refuses(() => Transactions.Update(live.TransactionId, new UpdateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = me.Id,
            RuleVersionId = archived.RuleVersionId
        }, Ct).AsTask());
    }

    [Fact]
    public async Task An_expense_cannot_be_moved_out_of_an_archived_group()
    {
        var live = await AGroup("Live");
        var archived = await AGroup("Archived");

        await Groups.Archive(archived.GroupId, Ct);

        var me = GetService<ICurrentUser>().User;

        await Refuses(() => Transactions.Update(archived.TransactionId, new UpdateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = me.Id,
            RuleVersionId = live.RuleVersionId
        }, Ct).AsTask());
    }

    // ---- and everything that is not a write --------------------------------------------------

    [Fact]
    public async Task An_archived_group_still_reads()
    {
        var (groupId, ruleId, _, transactionId) = await AGroup();
        await Groups.Archive(groupId, Ct);

        Assert.NotNull(await (await Groups.GetGroupById(groupId, Ct)).FirstOrDefaultAsync(Ct));
        Assert.Single(await (await Groups.GetGroupMembers(groupId, Ct)).ToListAsync(Ct));
        Assert.NotNull(await Transactions.GetDetails(transactionId, Ct));
        Assert.NotNull(await Rules.GetRuleDetails(ruleId, Ct));

        var balances = await (await Groups.GetGroupNetBalance(groupId, Ct)).ToListAsync(Ct);
        Assert.Single(balances);

        var page = await (await Transactions.List(Ct))
            .Where(t => t.RuleVersion.Rule.Group.Id == groupId)
            .ToTransactionPageAsync(new TransactionFilter(), new SortRequest(), new PageRequest(), Ct);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Unarchiving_lets_the_writes_through_again()
    {
        var (groupId, _, ruleVersionId, _) = await AGroup();

        await Groups.Archive(groupId, Ct);
        await Groups.Unarchive(groupId, Ct);

        var created = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Dinner",
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = groupId,
            RuleVersionId = ruleVersionId
        }, Ct);

        Assert.NotEqual(Guid.Empty, created.Id);

        await Groups.UpdateGroup(groupId, new CreateGroupRequest { Name = "Back again" }, Ct);
    }

    /// <summary>
    /// Leaving is not a change to the group's ledger, and someone deleting their account
    /// should not be held by a group nobody is using any more. Archiving does not block it.
    /// </summary>
    [Fact]
    public async Task An_archived_group_does_not_hold_on_to_someone_deleting_their_account()
    {
        var (groupId, _, _, _) = await AGroup();

        var other = await CreateNewUser();
        await Groups.AddGroupMembers(groupId,
            new AddMemberRequest([new UserIdentifier { Email = other.Email! }]), Ct);

        await Groups.Archive(groupId, Ct);

        var outstanding = await GetService<IAccountService>().DeleteAccount(other.Id, Ct);

        Assert.Empty(outstanding);
    }
}

using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.API.Test.Base;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Test.Group;

/// <summary>
/// Leaving a group, and what it does not take with it.
/// </summary>
/// <remarks>
/// The first cut hid everything the leaver had paid in the group from their own listing and
/// totals, because the listing was scoped to the groups they were in. Leaving a flat share
/// should not erase a year of your own spending from your own records. So what you paid
/// stays readable to you; what you may no longer do is change it, because a change moves
/// balances for people whose group you have left.
/// </remarks>
public class GroupLeaveTest(ApiTestFixture fixture) : ApiUnitTest(fixture)
{
    private IGroupService Groups => GetService<IGroupService>();
    private ITransactionService Transactions => GetService<ITransactionService>();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A group of two where the caller paid 20 split evenly, the other member has paid them
    /// back, and the balance is therefore zero -- the state in which leaving is allowed.
    /// </summary>
    private async Task<(Guid GroupId, Guid ExpenseId, Data.Entities.User Other)> ASettledGroupWithMyExpense()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Flat" }, Ct);
        var other = await CreateNewUser();
        await JoinGroup(group.Id, other);

        var expense = await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Groceries",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id
        }, Ct);

        await Groups.Settle(group.Id, new SettleRequest { UserId = other.Id, Amount = 10m }, Ct);

        return (group.Id, expense.Id, other);
    }

    [Fact]
    public async Task Leaving_takes_the_caller_out_of_the_group()
    {
        var (groupId, _, other) = await ASettledGroupWithMyExpense();

        await Groups.Leave(groupId, Ct);

        Assert.Empty(await Groups.GetGroupById(groupId, Ct));

        var members = await DbContext.Set<Data.Entities.Group>()
            .Where(candidate => candidate.Id == groupId)
            .SelectMany(candidate => candidate.Users)
            .Select(user => user.Id)
            .ToListAsync(Ct);

        Assert.Equal([other.Id], members);
    }

    [Fact]
    public async Task What_I_paid_there_is_still_in_my_own_listing_after_I_leave()
    {
        var (groupId, expenseId, _) = await ASettledGroupWithMyExpense();

        await Groups.Leave(groupId, Ct);

        var mine = await (await Transactions.List(Ct)).ToListAsync(Ct);

        Assert.Contains(mine, expense => expense.Id == expenseId);
        Assert.NotNull(await Transactions.GetDetails(expenseId, Ct));
    }

    /// <summary>
    /// The group's own listing is the group's, not the leaver's: once out, they do not see
    /// it through that door even for the rows that are theirs.
    /// </summary>
    [Fact]
    public async Task The_groups_listing_no_longer_answers_me_after_I_leave()
    {
        var (groupId, _, _) = await ASettledGroupWithMyExpense();

        Assert.NotEmpty(await (await Transactions.InGroup(groupId, Ct)).ToListAsync(Ct));

        await Groups.Leave(groupId, Ct);

        Assert.Empty(await (await Transactions.InGroup(groupId, Ct)).ToListAsync(Ct));
    }

    [Fact]
    public async Task An_expense_in_a_group_I_left_can_be_read_but_not_changed()
    {
        var (groupId, expenseId, _) = await ASettledGroupWithMyExpense();
        var me = GetService<ICurrentUser>().User;

        await Groups.Leave(groupId, Ct);

        var edit = await Assert.ThrowsAsync<ConflictException>(() =>
            Transactions.Update(expenseId, new UpdateTransactionRequest
            {
                Name = "Groceries, again",
                Amount = 20m,
                DateTime = DateTimeOffset.UtcNow,
                PaidByUserId = me.Id,
                GroupId = groupId
            }, Ct).AsTask());

        Assert.Equal(ErrorCodes.TransactionGroupLeft, edit.Code);

        var delete = await Assert.ThrowsAsync<ConflictException>(() => Transactions.Delete(expenseId, Ct));

        Assert.Equal(ErrorCodes.TransactionGroupLeft, delete.Code);

        // And it is still there, unchanged.
        var details = await Transactions.GetDetails(expenseId, Ct);
        Assert.Equal("Groceries", details!.Name);
    }

    /// <summary>
    /// Previewing that edit refuses the way the save does, rather than reporting the group
    /// missing.
    /// </summary>
    /// <remarks>
    /// The preview is worth having only because it meets the save's refusal while there is
    /// still a field on screen to fix it in, so answering a different refusal is worse than
    /// answering none. A leaver can still read the expense -- it is their own record -- and
    /// without the membership check the group lookup is the first thing to notice anything
    /// is wrong and calls the group missing: a 404 about the group, for a caller whose
    /// problem is that they left it.
    /// </remarks>
    [Fact]
    public async Task Previewing_an_edit_in_a_group_I_left_refuses_the_way_the_save_does()
    {
        var (groupId, expenseId, _) = await ASettledGroupWithMyExpense();
        var me = GetService<ICurrentUser>().User;

        await Groups.Leave(groupId, Ct);

        var request = new UpdateTransactionRequest
        {
            Name = "Groceries, again",
            Amount = 20m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = me.Id,
            GroupId = groupId
        };

        var preview = await Assert.ThrowsAsync<ConflictException>(() =>
            Transactions.PreviewUpdate(expenseId, request, ct: Ct));

        var save = await Assert.ThrowsAsync<ConflictException>(() =>
            Transactions.Update(expenseId, request, Ct).AsTask());

        Assert.Equal(ErrorCodes.TransactionGroupLeft, preview.Code);
        Assert.Equal(save.Code, preview.Code);
    }

    [Fact]
    public async Task Leaving_with_a_balance_is_refused_and_names_it()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Trip" }, Ct);
        await JoinGroup(group.Id, await CreateNewUser());

        await Transactions.Create(new CreateTransactionRequest
        {
            Name = "Hotel",
            Amount = 100m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id
        }, Ct);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Groups.Leave(group.Id, Ct));

        Assert.Equal(ErrorCodes.GroupMemberNotSettled, refusal.Code);
        Assert.Equal(50m, refusal.Extensions["balance"]);

        Assert.NotEmpty(await Groups.GetGroupById(group.Id, Ct));
    }

    /// <summary>
    /// A group with nobody in it is invisible to everybody and still holds the history the
    /// last member is walking away from. Archiving is what they want, and it is one tap away.
    /// </summary>
    [Fact]
    public async Task The_last_member_cannot_leave()
    {
        var group = await Groups.CreateGroup(new CreateGroupRequest { Name = "Just me" }, Ct);

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => Groups.Leave(group.Id, Ct));

        Assert.Equal(ErrorCodes.GroupCannotLeaveLastMember, refusal.Code);
    }

    [Fact]
    public async Task Leaving_a_group_I_am_not_in_is_a_404()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Groups.Leave(Guid.NewGuid(), Ct));
    }
}

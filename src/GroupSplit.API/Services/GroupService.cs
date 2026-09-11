using GroupSplit.Data;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// One member's balance in one group, with the group named on the row.
/// </summary>
/// <remarks>
/// <see cref="GroupNetBalance"/> with the group attached, and not a replacement for it: a
/// group's own balances view already knows which group it is looking at, and putting the
/// name on every row there would be the same string repeated down the page. This exists
/// for the one read that spans groups.
/// </remarks>
public record GroupMemberBalance(
    Guid GroupId,
    string GroupName,
    Guid UserId,
    string UserName,
    decimal AmountPaid,
    decimal AmountOwed,
    bool IsPendingInvitee = false)
{
    public decimal Balance => AmountPaid - AmountOwed;
}

public interface IGroupService
{
    /// <summary>
    /// Creates a new group for the current user.
    /// </summary>
    ValueTask<Group> CreateGroup(CreateGroupRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all groups for the current user.
    /// </summary>
    Task<IQueryable<Group>> GetAllGroups(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a group by ID for the current user.
    /// </summary>
    Task<IQueryable<Group>> GetGroupById(Guid groupId, CancellationToken cancellationToken = default);

    Task<CreateGroupRequest?> GetUpdateModel(Guid id, CancellationToken ct = default);

    ValueTask<Group> UpdateGroup(Guid groupId, CreateGroupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Everybody a group can point at: its members, and the people it has invited and is
    /// still waiting on.
    /// </summary>
    /// <remarks>
    /// Wider than the name suggests, on purpose, because this is what every screen that
    /// offers a choice of person reads -- a payer, a share, a rule's participants -- and an
    /// invitee is choosable in all three. Which of them has actually joined is on the rows:
    /// <see cref="UserInfo.IsPendingInvitee"/>. Nothing here decides who may read or change
    /// the group; that is membership, and it is asked of <c>Group.Users</c> alone.
    /// </remarks>
    Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default);



    /// <summary>
    /// Removes a member from a group by ID and user ID
    /// </summary>
    Task<IQueryable<Group>> RemoveGroupMember(Guid groupId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops <paramref name="user"/> from <paramref name="group"/> and out of the split
    /// rules that name them, so later expenses stop giving a share to somebody who has
    /// left.
    /// <para>
    /// Deliberately does not save. Callers batch it with their own changes, which is what
    /// lets an account leaving several groups at once apply as one unit instead of
    /// stopping half way through. It also does not check the balance: whether leaving is
    /// allowed at all is the caller's question, and the caller deleting an account has to
    /// ask it of every group up front.
    /// </para>
    /// </summary>
    Task DetachMember(Group group, User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything that has happened in a group -- what it spent and who has paid whom --
    /// scoped to a group the caller belongs to.
    /// </summary>
    /// <remarks>
    /// The one read that deliberately sees transfers. Every other surface asks for
    /// <c>Set&lt;Expense&gt;()</c> and therefore cannot, which is what stopped settlements
    /// turning up as negative expenses in the lists and the totals. A history is a
    /// different question from a spending list, though, and "Omar paid you 40" is the row
    /// somebody looks for when the balance moves.
    /// </remarks>
    Task<IQueryable<Transaction>> GetGroupActivity(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything that has happened anywhere the caller is: every group they belong to, and
    /// their own expenses outside all of them.
    /// </summary>
    /// <remarks>
    /// The home page's feed. A group's activity answers "what has happened here"; this
    /// answers "what has happened", which is the question somebody opening the app actually
    /// has. Until now the nearest thing to it was the listing of expenses they had paid for
    /// personally -- one slice of the answer, and the slice that leaves out both the money
    /// other people are spending on their behalf and every settlement.
    /// </remarks>
    Task<IQueryable<Transaction>> GetUserActivity(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the balance of a group per user
    /// </summary>
    Task<IQueryable<GroupNetBalance>> GetGroupNetBalance(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same balances, for a group <paramref name="memberId"/> belongs to rather than
    /// one the caller belongs to.
    /// <para>
    /// Needed by anything acting on somebody else's behalf. Going through
    /// <see cref="GetGroupNetBalance"/> instead would scope the lookup to the caller's own
    /// groups, so a group they are not in yields no rows at all -- which reads as a
    /// settled balance rather than as an answer that could not be given.
    /// </para>
    /// </summary>
    Task<IQueryable<GroupNetBalance>> GetGroupNetBalanceFor(Guid groupId, Guid memberId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every member balance in every group the caller is in, each row saying which group it
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// The input to the cross-group settlement plan. That plan is the per-group minimisation
    /// run over each group and then added up per person, so it needs the whole picture in
    /// one read rather than a query per group: the alternative is one round trip per group
    /// somebody is in, on the screen they open to square up.
    /// <para>
    /// Archived groups are in it. Archiving tidies somebody's list; it does not forgive a
    /// debt, and a settle screen that quietly dropped one would be telling them they are
    /// square when they are not.
    /// </para>
    /// </remarks>
    Task<IQueryable<GroupMemberBalance>> GetAllGroupNetBalances(CancellationToken cancellationToken = default);

    Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the caller out of a group they are in.
    /// </summary>
    /// <remarks>
    /// The same rule as being removed by somebody else -- settle up first -- applied to the
    /// one person who could not do it before. Removing a member was somebody else's action
    /// on you, and the endpoint that did it refused to act on yourself, so the debtor with
    /// nothing left owing had no way out of a group at all.
    /// </remarks>
    Task Leave(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the caller stands across every group they are in, netted per group and then
    /// added up.
    /// </summary>
    Task<UserPositionResponse> GetPosition(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the group out of the caller's own list, the way archiving a note does.
    /// Nothing about the group changes and no other member is affected: it goes on
    /// accepting every write it would otherwise accept. Archiving twice is not an error.
    /// </summary>
    Task<IQueryable<Group>> Archive(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Brings it back into the caller's list. Also idempotent.</summary>
    Task<IQueryable<Group>> Unarchive(Guid groupId, CancellationToken cancellationToken = default);
}

public class GroupService(
    ICurrentUser userContext,
    AppDbContext context,
    IGroupParticipants participants,
    ISplitRuleRevisions revisions) : IGroupService
{
    public async ValueTask<Group> CreateGroup(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var user = userContext.User;

        var group = new Group
        {
            Users =
            {
                user
            },
            Name = request.Name
        };

        context.Add(group);
        await context.SaveChangesAsync(cancellationToken);

        // The join row is EF's to write, so the payload on it is set afterwards, on the row
        // that now exists.
        var membership = await context.Set<GroupMembership>()
            .FirstOrDefaultAsync(row => row.GroupId == group.Id && row.UserId == user.Id, cancellationToken);

        if (membership is not null)
        {
            membership.JoinedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }

        return group;
    }

    public async Task<IQueryable<Group>> GetAllGroups(CancellationToken cancellationToken = default)
    {
        var user = userContext.User;

        // Load the user's groups
        return GroupsOf(user.Id);
    }

    /// <summary>
    /// The groups one user belongs to. The caller says whose, so this is the one place
    /// that does not assume the answer is about whoever is signed in.
    /// </summary>
    private IQueryable<Group> GroupsOf(Guid userId) =>
        context.Set<Group>()
            .Where(g => g.Users.Any(u => u.Id == userId));

    public async Task<IQueryable<Group>> GetGroupById(Guid groupId, CancellationToken cancellationToken = default)
    {
        return from g in await GetAllGroups(cancellationToken)
               where g.Id == groupId
               select g;
    }

    public async Task<CreateGroupRequest?> GetUpdateModel(Guid id, CancellationToken ct = default)
    {
        return await (from t in await GetGroupById(id, ct)
                      select new CreateGroupRequest
                      {
                          Name = t.Name
                      }).FirstOrDefaultAsync(ct);
    }


    public async ValueTask<Group> UpdateGroup(Guid groupId, CreateGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await GetGroupById(groupId, cancellationToken);

        var existingGroup = await group.FirstOrDefaultAsync(cancellationToken);

        if (existingGroup is null)
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        existingGroup.Name = request.Name;

        await context.SaveChangesAsync(cancellationToken);

        return existingGroup;
    }

    public async Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default)
    {
        // Still scoped through the caller's own groups, so a group they are not in answers
        // with nothing rather than with its roster.
        var mine = await GetGroupById(groupId, cancellationToken);

        return from user in participants.Of(groupId)
               where mine.Any(candidate => candidate.Id == groupId)
               select user;
    }

    public async Task<IQueryable<Group>> RemoveGroupMember(Guid groupId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var currentUser = userContext.User;
        if (userId == currentUser.Id)
            throw new ForbiddenException(ErrorCodes.GroupCannotRemoveSelf, "You cannot remove yourself from a group.");

        var groupQuery = (await GetGroupById(groupId, cancellationToken)).Include(g => g.Users);
        var group = await groupQuery.FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        // Before the balance check below: "the group is archived" is the more useful thing
        // to hear, and it is the one the person can act on.
        var user = await context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
            throw new NotFoundException(ErrorCodes.UserNotFound, "User was not found.");

        // The members list now shows the people the group is waiting on too, so this can be
        // asked of one of them. They are not a member and there is nothing to remove:
        // withdrawing the invitation is the act, and it says what becomes of anything
        // recorded against them.
        if (await participants.IsPendingInvitee(groupId, userId, cancellationToken))
            throw new ConflictException(ErrorCodes.GroupMemberNotJoined,
                "That person has been invited and has not joined, so there is no membership to " +
                "remove. Withdraw the invitation instead.");

        var groupBalances = await GetGroupNetBalance(groupId, cancellationToken);
        var userBalance = await groupBalances.Where(gb => gb.UserId == userId)
            .Select(x => x.Balance)
            .FirstOrDefaultAsync(cancellationToken);

        if (userBalance is not 0)
            throw new ConflictException(ErrorCodes.GroupMemberNotSettled, "The member has to settle up before leaving the group.")
                .WithExtension("balance", userBalance);

        await DetachMember(group, user, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        return groupQuery;
    }

    /// <summary>
    /// Takes a member out of a group, and out of the rules that name them.
    /// </summary>
    /// <remarks>
    /// A rule that still named a departed member would keep giving them a share of every
    /// later expense -- and for a deleted account that would go on distorting what everyone
    /// still in the group owes. Removing them is enough on its own: weights are
    /// proportional and the division normalises by whatever total it is given, so what was
    /// theirs is redistributed among the rest rather than leaving a hole.
    /// <para>
    /// A new version of each rule that named them, rather than their places deleted out of
    /// the version those rules are on. The rule has to stop naming them going forward; the
    /// version an expense from last March was divided by has to go on saying what it said in
    /// March, or that expense can no longer be divided again by the rule it had. Both are
    /// true of a new version and neither is true of an edited one.
    /// </para>
    /// <para>
    /// Recorded expenses are untouched either way: they hold both the amounts they were
    /// divided into and the version that divided them, so a departure cannot restate what
    /// anybody owed.
    /// </para>
    /// </remarks>
    public async Task DetachMember(Group group, User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(user);

        group.Users.Remove(user);

        await revisions.WithoutParticipant(group.Id, user.Id, toUserId: null, cancellationToken);
    }

    public async Task<IQueryable<Transaction>> GetGroupActivity(Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var groups = await GetGroupById(groupId, cancellationToken);

        return from transaction in context.Set<Transaction>()
               where groups.Any(@group => @group.Id == transaction.GroupId)
               select transaction;
    }

    public async Task<IQueryable<Transaction>> GetUserActivity(
        CancellationToken cancellationToken = default)
    {
        var user = userContext.User;
        var groups = await GetAllGroups(cancellationToken);

        // The same two clauses the expense listing uses -- anything in a group they are in,
        // plus anything they paid for wherever it is -- over transactions rather than
        // expenses, so settlements are in it. That is the whole difference: a balance that
        // moved with nothing in the feed to explain it is its own kind of wrong.
        return from transaction in context.Set<Transaction>()
               where groups.Any(@group => @group.Id == transaction.GroupId) ||
                     transaction.UserId == user.Id
               select transaction;
    }

    public async Task<IQueryable<GroupNetBalance>> GetGroupNetBalance(Guid groupId,
        CancellationToken cancellationToken = default)
    {
        return NetBalances(await GetGroupById(groupId, cancellationToken));
    }

    public Task<IQueryable<GroupNetBalance>> GetGroupNetBalanceFor(Guid groupId, Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var groupQuery = GroupsOf(memberId).Where(g => g.Id == groupId);

        return Task.FromResult(NetBalances(groupQuery));
    }

    public async Task<IQueryable<GroupMemberBalance>> GetAllGroupNetBalances(
        CancellationToken cancellationToken = default)
    {
        var groups = await GetAllGroups(cancellationToken);

        // The same two sums <see cref="NetBalances"/> takes, with the group carried through.
        // Transfers are in both of them, because a transfer is a transaction with one split:
        // the payer's paid rises and the payee's owed rises, which is what paying somebody
        // back does to a balance.
        return from @group in groups
               from user in context.Set<User>()
               where user.Groups.Any(candidate => candidate.Id == @group.Id) ||
                     context.Set<GroupInvitation>().Any(invitation =>
                         invitation.GroupId == @group.Id && invitation.ParticipantUserId == user.Id)
               select new GroupMemberBalance(
                   @group.Id,
                   @group.Name,
                   user.Id,
                   // People.Display, written out longhand: this is built in SQL, so the
                   // fallback to the address has to be too.
                   user.FirstName == null && user.LastName == null
                       ? user.Email ?? ""
                       : user.FirstName + (user.LastName != null ? " " + user.LastName : ""),
                   (from transaction in context.Set<Transaction>()
                    where transaction.GroupId == @group.Id && transaction.UserId == user.Id
                    select transaction.Amount).Sum(),
                   (from split in context.Set<TransactionSplit>()
                    where split.Transaction.GroupId == @group.Id && split.UserId == user.Id
                    select split.Amount).Sum(),
                   !user.Groups.Any(candidate => candidate.Id == @group.Id));
    }

    /// <summary>
    /// Builds the per-member balances over whatever group query it is handed, so the
    /// arithmetic -- and the truncation it depends on -- has one home no matter who is
    /// asking or on whose behalf.
    /// </summary>
    private IQueryable<GroupNetBalance> NetBalances(IQueryable<Group> groupQuery)
    {
        // Participants and not members, because the column has to add up. Shares are
        // recorded against an invited address from the moment it is invited, so a listing of
        // members alone would show a group whose balances did not sum to zero, with the
        // missing side belonging to nobody on the page.
        var groupBalance =
                    from @group in groupQuery
                    from user in context.Set<User>()
                    where user.Groups.Any(candidate => candidate.Id == @group.Id) ||
                          context.Set<GroupInvitation>().Any(invitation =>
                              invitation.GroupId == @group.Id && invitation.ParticipantUserId == user.Id)
                    select new GroupNetBalance
                    {
                        UserId = user.Id,
                        // People.Display, in SQL. The surname is appended only when there is
                        // one, where this used to add a space unconditionally: somebody the
                        // group named has a first name and nothing else, and "Carlos " read
                        // as a typo down the column.
                        UserName = user.FirstName == null && user.LastName == null
                            ? user.Email ?? ""
                            : user.FirstName + (user.LastName != null ? " " + user.LastName : ""),
                        AmountPaid = (from transaction in context.Set<Transaction>()
                                      where transaction.GroupId == @group.Id && transaction.User == user
                                      select transaction.Amount).Sum(),
                        AmountOwed = (from split in context.Set<TransactionSplit>()
                                      where split.Transaction.GroupId == @group.Id && split.User == user
                                      select split.Amount).Sum(),
                        IsPendingInvitee = !user.Groups.Any(candidate => candidate.Id == @group.Id)
                    } into balance
                    select new GroupNetBalance
                    {
                        UserId = balance.UserId,
                        UserName = balance.UserName,
                        AmountPaid = balance.AmountPaid,
                        AmountOwed = balance.AmountOwed,
                        Balance = balance.AmountPaid - balance.AmountOwed,
                        IsPendingInvitee = balance.IsPendingInvitee
                    };

        return groupBalance;
    }

    public async Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUser = userContext.User;

        var groupQuery = from @group in await GetGroupById(groupId, cancellationToken)
                         from groupUser in (from groupUser in @group.Users
                                            where groupUser.Id == request.UserId
                                            select groupUser).DefaultIfEmpty()
                         select new
                         {
                             Group = @group,
                             User = groupUser
                         };

        var result = await groupQuery.FirstOrDefaultAsync(cancellationToken);

        if (result is not { Group: { } resultGroup, User: var user })
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        // Before the missing-member check below, which is what somebody the group has
        // invited and is waiting on would otherwise fall into: they are not in Users, so the
        // join above finds nothing for them. Their balance is real and is on the group's
        // balances page, so "not found" would be a lie about a person the caller can see.
        // What is missing is the other end of the payment.
        if (await participants.IsPendingInvitee(groupId, request.UserId, cancellationToken))
            throw new ConflictException(ErrorCodes.SettlementWithPendingInvitee,
                "That person has been invited to the group and has not joined yet, so there is " +
                "nobody to settle up with. Their balance stands until they accept.");

        if (user is null)
            throw new NotFoundException(ErrorCodes.UserNotFound, "User was not found.");

        if (user.Id == currentUser.Id)
            throw new ConflictException(ErrorCodes.SettlementWithSelf, "A settlement needs two different people.");

        // Both sides can record one now, and which side this is comes from the request
        // rather than from the balance. Under "owed to you" the caller is the creditor and
        // the money moves from the other member to them; under "you owe" they are the
        // debtor saying they have paid, and it moves the other way. Reading the direction
        // off the balance instead would get the debtor's case exactly backwards -- they are
        // acting precisely while the balance still says they owe.
        //
        // One row either way, where a settlement used to be a matched pair of a positive
        // and a negative transaction hung off a pseudo-rule; a pair is two chances to write
        // half a settlement.
        var (from, to) = request.Direction is SettlementDirection.YouPaidThem
            ? (currentUser, user)
            : (user, currentUser);

        // The stated date, or now. A payment made on the 30th and typed in on the 3rd
        // belongs to the month it settled: without this the recorded moment was the only
        // moment available, so a group closing September could not put September's payments
        // in September.
        var date = (request.Date ?? DateTimeOffset.UtcNow).ToUniversalTime();

        var transfer = resultGroup.SettlementBetween(from, to, request.Amount, date,
            request.Description?.Trim() is { Length: > 0 } note ? note : null);

        context.Add(transfer);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task Leave(Guid groupId, CancellationToken cancellationToken = default)
    {
        var user = userContext.User;

        var group = await (await GetGroupById(groupId, cancellationToken))
                        .Include(candidate => candidate.Users)
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        // A group with nobody in it is invisible to everybody and still holds the history
        // its last member is walking away from. Archiving is what they actually want here,
        // and it is one tap away, so this says so rather than quietly orphaning the rows.
        if (group.Users.Count <= 1)
            throw new ConflictException(ErrorCodes.GroupCannotLeaveLastMember,
                "You are the only member left, so leaving would leave the group with nobody in it. Archive it instead.");

        var balance = await (await GetGroupNetBalance(groupId, cancellationToken))
            .Where(netBalance => netBalance.UserId == user.Id)
            .Select(netBalance => netBalance.Balance)
            .FirstOrDefaultAsync(cancellationToken);

        if (balance is not 0)
            throw new ConflictException(ErrorCodes.GroupMemberNotSettled,
                    "Settle up before leaving the group.")
                .WithExtension("balance", balance);

        await DetachMember(group, user, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<UserPositionResponse> GetPosition(CancellationToken cancellationToken = default)
    {
        var user = userContext.User;

        // Per group first, then added up, and the two directions kept apart. Netting them
        // into one number would say a person owed 25 in one group and owed 40 in another is
        // owed 15, which is true of nobody: they still have two people to square up with.
        var groups = await (
                from @group in GroupsOf(user.Id)
                join membership in context.Set<GroupMembership>()
                    on new { GroupId = @group.Id, UserId = user.Id }
                    equals new { membership.GroupId, membership.UserId }
                select new GroupPosition(
                    @group.Id,
                    @group.Name,
                    (from transaction in context.Set<Transaction>()
                        where transaction.GroupId == @group.Id && transaction.UserId == user.Id
                        select transaction.Amount).Sum() -
                    (from split in context.Set<TransactionSplit>()
                        where split.Transaction.GroupId == @group.Id && split.UserId == user.Id
                        select split.Amount).Sum(),
                    membership.ArchivedAt != null))
            .ToListAsync(cancellationToken);

        var owedToYou = groups.Where(position => position.Balance > 0).Sum(position => position.Balance);
        var youOwe = groups.Where(position => position.Balance < 0).Sum(position => -position.Balance);

        return new UserPositionResponse(
            owedToYou - youOwe,
            owedToYou,
            youOwe,
            [.. groups.OrderByDescending(position => Math.Abs(position.Balance))
                .ThenBy(position => position.GroupName)]);
    }

    public Task<IQueryable<Group>> Archive(Guid groupId, CancellationToken cancellationToken = default) =>
        SetArchivedAt(groupId, DateTimeOffset.UtcNow, cancellationToken);

    public Task<IQueryable<Group>> Unarchive(Guid groupId, CancellationToken cancellationToken = default) =>
        SetArchivedAt(groupId, null, cancellationToken);

    /// <summary>
    /// Both directions, because they differ only in the value written. It is the caller's
    /// own membership row that changes, so this says nothing about the group and nothing
    /// about anybody else in it.
    /// </summary>
    private async Task<IQueryable<Group>> SetArchivedAt(Guid groupId, DateTimeOffset? archivedAt,
        CancellationToken cancellationToken)
    {
        var user = userContext.User;

        var groupQuery = await GetGroupById(groupId, cancellationToken);

        // Membership rather than the group: a group the caller is not in has no membership
        // row for them, and answers the same way a missing one does.
        var membership = await context.Set<GroupMembership>()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == user.Id, cancellationToken);

        if (membership is null)
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        if (membership.ArchivedAt.HasValue != archivedAt.HasValue)
        {
            membership.ArchivedAt = archivedAt;
            await context.SaveChangesAsync(cancellationToken);
        }

        return groupQuery;
    }
}

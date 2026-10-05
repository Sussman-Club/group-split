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
    bool IsGuest = false)
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
    /// Everybody a group can point at: its members, and its guests.
    /// </summary>
    /// <remarks>
    /// Wider than the name suggests, on purpose, because this is what every screen that
    /// offers a choice of person reads -- a payer, a share, a rule's participants -- and a
    /// guest is choosable in all three. Which of them is a guest is on the rows:
    /// <see cref="UserInfo.IsGuest"/>. Nothing here decides who may read or change the group;
    /// that is membership, and it is asked of <c>Group.Users</c> alone.
    /// </remarks>
    Task<IQueryable<User>> GetGroupMembers(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// People who are no longer members or guests but still appear in the group's recorded
    /// history: members who left or were removed, and removed guests.
    /// </summary>
    /// <remarks>
    /// Read from the ledger rather than from closed membership rows: departures from before
    /// those rows were kept left nothing else behind, and somebody the ledger does not name
    /// has nothing on the page to explain. A read-only context list, never a participant
    /// list for new money.
    /// </remarks>
    Task<IQueryable<User>> GetGroupPastMembers(Guid groupId, CancellationToken cancellationToken = default);



    /// <summary>
    /// Removes a member or a guest from a group, once they are square with it. A member's
    /// membership is closed; a guest stops being one. Either way they stay in the group's
    /// history as somebody who has gone.
    /// </summary>
    Task<IQueryable<Group>> RemoveGroupMember(Guid groupId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends <paramref name="user"/>'s membership of <paramref name="group"/> and takes them
    /// out of the split rules that name them, so later expenses stop giving a share to
    /// somebody who has left.
    /// <para>
    /// The membership row is closed rather than deleted: <paramref name="reason"/> and
    /// <paramref name="byUserId"/> are written onto it with the moment, which is what lets a
    /// link shared while they were still in the group be refused to them afterwards.
    /// </para>
    /// <para>
    /// Deliberately does not save. Callers batch it with their own changes, which is what
    /// lets an account leaving several groups at once apply as one unit instead of
    /// stopping half way through. It also does not check the balance: whether leaving is
    /// allowed at all is the caller's question, and the caller deleting an account has to
    /// ask it of every group up front.
    /// </para>
    /// </summary>
    Task DetachMember(Group group, User user, LeftReason reason, Guid? byUserId,
        CancellationToken cancellationToken = default);

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
    ISplitRuleRevisions revisions,
    IMemberSplitRules memberRules,
    IBillSplitRule billRule,
    IEvenSplitRule evenRule,
    IJoinLinkService joinLinks) : IGroupService
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

        // The first member's own rule: all of it is for them. Everybody who joins later
        // gets theirs from IGroupJoiner, which is the only other way into a group.
        await memberRules.EnsureFor(group, user, cancellationToken);

        // And the one the group keeps for dividing an expense by its own receipt. One per
        // group, given rather than written, because it holds no settings for a second one
        // to differ by.
        await billRule.EnsureFor(group, cancellationToken);

        group.DefaultSplitRule = await evenRule.EnsureFor(group, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

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
                          Name = t.Name,
                          DefaultSplitRuleId = t.DefaultSplitRuleId
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

        // The property was added to the existing update contract, so callers from before the
        // change omit it.  Omission must preserve the group's default; the UI always names a
        // new one and never offers an unset state.
        if (request.DefaultSplitRuleId is { } ruleId)
        {
            // Projected rather than loaded: the question is one fact about the rule's
            // current version, and a rule edited a dozen times carries a dozen versions
            // nothing here reads.
            var rule = await context.Set<SplitRule>()
                           .Where(candidate => candidate.Id == ruleId && candidate.Group.Id == existingGroup.Id)
                           .Select(candidate => new
                           {
                               candidate.Name,
                               DividesByBill = candidate.Versions.Any(version =>
                                   version.SupersededAt == null && version is ItemizedSplitRuleVersion)
                           })
                           .FirstOrDefaultAsync(cancellationToken)
                       ?? throw new NotFoundException(ErrorCodes.SplitRuleNotFound,
                           "Split rule was not found in this group.");

            // Every rule in the group except one can serve as the fallback. "Divide it by
            // the bill" divides nothing itself -- it defers to the rule on each line of a
            // receipt -- so a group holding it as its default could not record an expense
            // with no receipt at all: every one of them would be refused for want of a bill
            // it was never going to have. The picker leaves it out for this reason, and a
            // caller reaching past the picker gets told rather than obeyed.
            if (rule.DividesByBill)
            {
                throw new ValidationException(ErrorCodes.SplitRuleInvalid,
                        $"\"{rule.Name}\" divides an expense by its own bill, so it cannot be "
                        + "the group's default: an expense with no receipt has nothing for it to "
                        + "read. Choose a rule that divides between people.")
                    .WithExtension("splitRuleId", ruleId);
            }

            existingGroup.DefaultSplitRuleId = ruleId;
        }

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

    public async Task<IQueryable<User>> GetGroupPastMembers(Guid groupId,
        CancellationToken cancellationToken = default)
    {
        // Scope through the caller's active membership first. A past member can be shown
        // only from a group the caller can still read, just like the ordinary roster.
        var mine = await GetGroupById(groupId, cancellationToken);

        return from user in context.Set<User>()
               where mine.Any(candidate => candidate.Id == groupId) &&
                     !user.Groups.Any(candidate => candidate.Id == groupId) &&
                     // A current guest is a participant, not history -- and a removed one,
                     // filtered out of this, is history.
                     !context.Set<GroupGuest>().Any(guest =>
                         guest.GroupId == groupId && guest.ParticipantUserId == user.Id) &&
                     // A claimed guest's stand-in is nobody: the person is a member under
                     // their own account, and an old rule version naming the stand-in is not
                     // a reason to list them twice. Asked of the history rather than with the
                     // guest filter off, which would turn it off for the whole query.
                     !context.Set<GroupMembershipEvent>().Any(change =>
                         change.GroupId == groupId && change.UserId == user.Id &&
                         change.Kind == MembershipEventKind.GuestClaimed) &&
                     (context.Set<Transaction>().Any(transaction =>
                          transaction.GroupId == groupId && transaction.UserId == user.Id) ||
                      context.Set<TransactionSplit>().Any(split =>
                          split.Transaction.GroupId == groupId && split.UserId == user.Id) ||
                      context.Set<SplitRuleParticipant>().Any(participant =>
                          participant.SplitRuleVersion.SplitRule.Group.Id == groupId &&
                          participant.UserId == user.Id) ||
                      context.Set<SoleSplitRuleVersion>().Any(version =>
                          version.SplitRule.Group.Id == groupId && version.UserId == user.Id))
               orderby user.FirstName, user.LastName, user.Email
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

        // Who they are to this group decides what there is to remove: a member's membership,
        // or a guest's place. Anybody else -- somebody who has already gone, or was never
        // here -- has nothing to remove, and this used to answer them with a success that
        // changed nothing.
        var standing = await participants.StandingOf(groupId, userId, cancellationToken);

        if (standing is ParticipantStanding.Departed or ParticipantStanding.Stranger)
            throw new NotFoundException(ErrorCodes.UserNotFound,
                "That person is not a member or guest of this group.");

        // The same rule for both: square first. Somebody who goes while owing or owed takes
        // that with them, and the group's balances would no longer add up among the people
        // still in it. For a guest the members record the repayment that squares them.
        var groupBalances = await GetGroupNetBalance(groupId, cancellationToken);
        var userBalance = await groupBalances.Where(gb => gb.UserId == userId)
            .Select(x => x.Balance)
            .FirstOrDefaultAsync(cancellationToken);

        if (userBalance is not 0)
            throw new ConflictException(ErrorCodes.GroupMemberNotSettled,
                    standing is ParticipantStanding.Guest
                        ? "That guest has to be square with the group before they can be removed. Record what they paid or were paid back first."
                        : "The member has to settle up before leaving the group.")
                .WithExtension("balance", userBalance);

        if (standing is ParticipantStanding.Guest)
        {
            await DetachGuest(group, userId, currentUser.Id, cancellationToken);

            // The group's join link stays: a guest never had a way to read it.
            return groupQuery;
        }

        var user = await context.Set<User>().FirstAsync(u => u.Id == userId, cancellationToken);

        await DetachMember(group, user, LeftReason.Removed, currentUser.Id, cancellationToken);

        // And the group's live join link goes out with them. They could read it while they
        // were a member and may have passed it on; the rejoin rule stops them using it, but
        // not whoever they gave it to. Leaving does not do this: the link is the group's,
        // sitting in its thread for other people, and leaving expresses no distrust.
        //
        // Revoke saves, which is what writes the departure too: one round trip, so a removal
        // cannot land without its revocation or the other way round.
        await joinLinks.Revoke(groupId, cancellationToken);

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
    public async Task DetachMember(Group group, User user, LeftReason reason, Guid? byUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(user);

        // The row, not the collection: removing them from group.Users would delete it, and a
        // departure is something the group has to remember. The filter hides the closed row
        // from every read of Users and Groups from here on.
        var membership = await context.Set<GroupMembership>()
            .FirstAsync(row => row.GroupId == group.Id && row.UserId == user.Id, cancellationToken);

        membership.LeftAt = DateTimeOffset.UtcNow;
        membership.LeftReason = reason;
        membership.LeftByUserId = byUserId;

        await revisions.WithoutParticipant(
            group.Id, user.Id, toUserId: null, RuleHandling.Prune, cancellationToken);
    }

    /// <summary>
    /// Ends a guest's place in the group, keeps them in its history, and takes them out of the
    /// rules that name them. Saves.
    /// </summary>
    /// <remarks>
    /// The guest's row ends rather than going, the same choice a departure makes, and for the
    /// same reasons: the ledger still names them, and the group should still be able to say
    /// who they were. Once ended they read as departed everywhere a departed member does --
    /// their shares cannot be moved, they cannot be named for new money, and they show among
    /// the past participants. Their link stops working, and the rules stop naming them from
    /// their next version on, exactly as for a member who leaves.
    /// </remarks>
    private async Task DetachGuest(Group group, Guid userId, Guid byUserId, CancellationToken ct)
    {
        var guest = await context.Set<GroupGuest>()
            .FirstAsync(row => row.GroupId == group.Id && row.ParticipantUserId == userId, ct);

        guest.EndedAt = DateTimeOffset.UtcNow;
        guest.EndReason = GuestEndReason.Removed;
        guest.EndedByUserId = byUserId;

        await context.EndLink(guest, ct);

        await revisions.WithoutParticipant(group.Id, userId, toUserId: null, RuleHandling.Prune, ct);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Claimed, removed, or given a new link at the same moment: the guest is not what
            // this decision was made about, so it is not made.
            throw new ConflictException(ErrorCodes.GroupGuestChanged,
                "Somebody else changed this guest at the same moment. Look again before removing them.");
        }
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
                     context.Set<GroupGuest>().Any(guest =>
                         guest.GroupId == @group.Id && guest.ParticipantUserId == user.Id)
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
        // recorded against guests like anybody else, so a listing of members alone would show
        // a group whose balances did not sum to zero, with the missing side belonging to
        // nobody on the page.
        var groupBalance =
                    from @group in groupQuery
                    from user in context.Set<User>()
                    where user.Groups.Any(candidate => candidate.Id == @group.Id) ||
                          context.Set<GroupGuest>().Any(guest =>
                              guest.GroupId == @group.Id && guest.ParticipantUserId == user.Id)
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
                        IsGuest = !user.Groups.Any(candidate => candidate.Id == @group.Id)
                    } into balance
                    select new GroupNetBalance
                    {
                        UserId = balance.UserId,
                        UserName = balance.UserName,
                        AmountPaid = balance.AmountPaid,
                        AmountOwed = balance.AmountOwed,
                        Balance = balance.AmountPaid - balance.AmountOwed,
                        IsGuest = balance.IsGuest
                    };

        return groupBalance;
    }

    public async Task Settle(Guid groupId, SettleRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUser = userContext.User;

        var resultGroup = await (await GetGroupById(groupId, cancellationToken))
                              .FirstOrDefaultAsync(cancellationToken)
                          ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        // A participant, not only a member: a guest's balance is real and is on the group's
        // balances page, and the members record what a guest pays and is paid. Somebody who
        // has gone is not here to settle with.
        var user = await participants.Find(groupId, request.UserId, cancellationToken)
                   ?? throw new NotFoundException(ErrorCodes.UserNotFound, "User was not found.");

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

        await DetachMember(group, user, LeftReason.Left, user.Id, cancellationToken);

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

using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// What one hand-over moved, so whoever asked for it can be told.
/// </summary>
/// <param name="RulesEmptied">
/// How many of those rules have nobody left in them.
/// </param>
/// <remarks>
/// <paramref name="RulesEmptied"/> is the one number here that is a warning rather than a
/// receipt. A rule with nobody left in it has changed what it means: a shares or percentage
/// rule stops dividing at all and refuses the next expense filed under it, and an even rule
/// quietly becomes "between everybody", because naming nobody is how an even split says
/// that. Neither is a state anybody asked for, and until this was counted neither was
/// mentioned at the moment it was caused -- which
/// <c>docs/split-rules-and-membership.md</c> named as the gap worth closing.
/// </remarks>
/// <param name="SettlementsCancelled">
/// Repayments between the two people that the hand-over would have turned into somebody
/// paying themselves, and so deleted instead.
/// </param>
public record ParticipantHandover(
    int SharesMoved,
    decimal AmountOwed,
    int PaymentsMoved,
    decimal AmountPaid,
    int RulesAffected,
    int RulesEmptied = 0,
    int SettlementsCancelled = 0,
    decimal AmountCancelled = 0m)
{
    public static readonly ParticipantHandover Nothing = new(0, 0m, 0, 0m, 0);

    public bool MovedAnything => SharesMoved > 0 || PaymentsMoved > 0 || RulesAffected > 0;
}

/// <summary>
/// What a hand-over does with the rules that name the person moving.
/// </summary>
/// <remarks>
/// The two directions are opposites, and one code path served both until it was noticed.
/// Somebody <em>arriving</em> keeps their place in a rule -- the group wrote "Carlos gets
/// one share" and meant it, and Carlos claiming his link is the group getting what it
/// asked for. Somebody <em>leaving</em> loses it, because a rule that went on naming them
/// would keep giving them a share of every later expense.
/// </remarks>
public enum RuleHandling
{
    /// <summary>The rules follow the person: their weight becomes the receiver's.</summary>
    Transfer,

    /// <summary>
    /// The rules stop naming them, from their next version on. What was theirs is divided
    /// among the rest, since weights are proportional and the division normalises by
    /// whatever total it is given.
    /// </summary>
    Prune
}

/// <summary>
/// What one account is to one group. A fact about the pair, never an authorization answer
/// on its own: the services decide what each standing may do, and
/// <c>docs/participants.md</c> is the table of those decisions.
/// </summary>
public enum ParticipantStanding
{
    /// <summary>Holds an open membership. May read and change the group.</summary>
    Member,

    /// <summary>
    /// A current guest's stand-in. Choosable wherever money is recorded, settleable, and
    /// removable once square -- and nothing else: there is nobody to sign in as them.
    /// </summary>
    Guest,

    /// <summary>
    /// Was a member or a guest and is not now: a closed membership row, a removed guest, or
    /// -- for somebody who left before departures were kept -- a name the group's ledger or
    /// rules still carry.
    /// </summary>
    Departed,

    /// <summary>None of the above.</summary>
    Stranger
}

/// <summary>
/// Who a group may point at when it records money: its members, and its guests.
/// </summary>
/// <remarks>
/// The distinction this exists to keep straight. <em>Membership</em> is who may read and
/// change the group -- every authorization check in the app asks that question of
/// <c>Group.Users</c> and must go on asking it of nothing else. <em>Participation</em> is
/// who may be given a share, which is a wider set, because the people a group spends with
/// are not all going to sign up: the flat is shared and the trip is taken with whoever came.
/// <para>
/// So a guest is choosable wherever a person is chosen for money -- a transaction's payer,
/// a stated split, a split rule, either end of a repayment -- and nowhere else. They are not
/// in the group and cannot see it; the members record everything for them.
/// </para>
/// </remarks>
public interface IGroupParticipants
{
    /// <summary>
    /// Everyone a group may record money against, members and guests alike.
    /// </summary>
    /// <remarks>
    /// One query over the users with an <c>OR</c> rather than a union of two, so the rows
    /// cannot be duplicated by a person who somehow answers both halves -- which matters
    /// because this is what the group's balances are summed over, and a name appearing twice
    /// there would double what the group thinks it spent.
    /// </remarks>
    IQueryable<User> Of(Guid groupId);

    /// <summary>The same set, as ids, for the checks that only need to test membership of it.</summary>
    Task<IReadOnlyCollection<Guid>> IdsOf(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// The wire shape of a group's people, each row saying whether they are a guest.
    /// </summary>
    /// <remarks>
    /// Takes the query rather than the group's id because the scoping is the caller's --
    /// <c>GetGroupMembers</c> answers with nothing at all for a group the reader is not in --
    /// and only the flag belongs here. It is a fact about the pair and not about the person:
    /// a stand-in is a guest of exactly one group, and an account is a member of some groups
    /// and not others, so the group being read has to be said.
    /// </remarks>
    IQueryable<UserInfo> Describe(IQueryable<User> people, Guid groupId);

    /// <summary>
    /// The participant of <paramref name="groupId"/> with that id, or null when they are
    /// neither a member nor a guest.
    /// </summary>
    Task<User?> Find(Guid groupId, Guid userId, CancellationToken ct = default);

    /// <summary>Whether that person is one of the group's current guests.</summary>
    Task<bool> IsGuest(Guid groupId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// What <paramref name="userId"/> is to <paramref name="groupId"/>: a member, a guest,
    /// departed, or nobody.
    /// </summary>
    /// <remarks>
    /// Member wins over everything, because a current membership is the only standing that
    /// grants anything. Departed is asked last and of two sources: a closed membership row,
    /// which every departure writes now, and the ledger, which is what is left of a removed
    /// guest and of the departures from before membership rows were kept.
    /// </remarks>
    Task<ParticipantStanding> StandingOf(Guid groupId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Everybody the transaction names -- as its payer or in a share -- who is no longer a
    /// participant of its group, with what they hold on it. Empty for a personal one.
    /// </summary>
    /// <remarks>
    /// Read off the transaction rather than off membership rows, so it covers everybody who
    /// can be named and gone: a member who left, one removed, and a removed guest. Needs the
    /// splits loaded.
    /// </remarks>
    Task<IReadOnlyList<ParticipantPosition>> DepartedOn(Transaction transaction, CancellationToken ct = default);

    /// <summary>
    /// A new stand-in for a person a group has named: a row with that name on it, no
    /// address, and no identity, so nobody can sign in as it.
    /// </summary>
    /// <remarks>
    /// It exists to be given shares when there is no account to give them to. Claiming the
    /// guest's link is what puts a real person behind it, and claiming moves the position
    /// onto that person's own account and leaves this empty -- so a stand-in never holds
    /// anything for somebody who is already here.
    /// <para>
    /// Always new, and never an existing account: there is no address to recognise one by,
    /// and guessing from a name would be handing a stranger's ledger to whoever the group
    /// happened to call Dani.
    /// </para>
    /// <para>
    /// Added to the context and not saved. The caller is writing the guest in the same unit
    /// of work, and a stand-in with no guest behind it is a row nothing would ever point at.
    /// </para>
    /// </remarks>
    User StandInFor(string name);

    /// <summary>
    /// Hands everything a guest holds in a group to the account that claimed them: their
    /// shares, the transactions they were down as having paid for, and their place in the
    /// group's split rules.
    /// </summary>
    /// <remarks>
    /// Amounts are never touched. A share moves from one name to another, and where the
    /// receiver already had a share on the same transaction the two are added into one row
    /// -- because a transaction may hold only one opinion about what a person owed. So
    /// every transaction still sums to its own amount, which is the invariant the group's
    /// balances rest on, and the group's balances still sum to zero.
    /// <para>
    /// The rules follow them: the group wrote "Carlos gets one share" and meant it, so Carlos
    /// claiming his link is the group getting what it asked for.
    /// </para>
    /// <para>
    /// A repayment between the two of them would become one from the claimer to themselves
    /// -- a member who recorded "Pat paid me back" and then turns out to be Pat. Those are
    /// deleted rather than moved. They net to nothing for whoever holds both ends, so no
    /// balance changes, and a row saying somebody paid themselves would only confuse the
    /// ledger.
    /// </para>
    /// <para>
    /// Either way the rules change by gaining a version rather than by being edited, through
    /// <see cref="ISplitRuleRevisions"/>: the version an expense was divided by has to go on
    /// saying what it said when the expense was written.
    /// </para>
    /// <para>
    /// Does not save, so the caller can apply it together with whatever else the same
    /// action changes -- the guest's row ending, the claimer joining.
    /// </para>
    /// </remarks>
    Task<ParticipantHandover> HandOver(Guid groupId, Guid fromUserId, Guid toUserId,
        CancellationToken ct = default);
}

public sealed class GroupParticipants(
    AppDbContext context,
    ISplitRuleRevisions revisions,
    IMemberSplitRules memberRules) : IGroupParticipants
{
    public IQueryable<User> Of(Guid groupId) =>
        context.Set<User>()
            .Where(user => user.Groups.Any(@group => @group.Id == groupId) ||
                           context.Set<GroupGuest>().Any(guest =>
                               guest.GroupId == groupId &&
                               guest.ParticipantUserId == user.Id));

    public async Task<IReadOnlyCollection<Guid>> IdsOf(Guid groupId, CancellationToken ct = default) =>
        await Of(groupId).Select(user => user.Id).ToListAsync(ct);

    public IQueryable<UserInfo> Describe(IQueryable<User> people, Guid groupId) =>
        from user in people
        select new UserInfo(user.Id, user.FirstName, user.LastName, user.Email,
            // A participant who is not a member is a guest: that is what the listing is the
            // union of.
            !user.Groups.Any(@group => @group.Id == groupId));

    public Task<User?> Find(Guid groupId, Guid userId, CancellationToken ct = default) =>
        Of(groupId).FirstOrDefaultAsync(user => user.Id == userId, ct);

    /// <remarks>
    /// No membership check beside it, because the two sets cannot overlap: a guest's
    /// participant is a stand-in the group made, and nobody can sign in as one. So being a
    /// current guest's stand-in is the whole of the question.
    /// </remarks>
    public Task<bool> IsGuest(Guid groupId, Guid userId, CancellationToken ct = default) =>
        context.Set<GroupGuest>()
            .AnyAsync(guest => guest.GroupId == groupId &&
                               guest.ParticipantUserId == userId, ct);

    public async Task<ParticipantStanding> StandingOf(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        // With the filter off, so a closed row answers too; the open one is told apart by its
        // date.
        var membership = await context.Set<GroupMembership>()
            .IgnoreQueryFilters()
            .Where(row => row.GroupId == groupId && row.UserId == userId)
            .Select(row => new { row.LeftAt })
            .FirstOrDefaultAsync(ct);

        if (membership is { LeftAt: null })
            return ParticipantStanding.Member;

        if (await IsGuest(groupId, userId, ct))
            return ParticipantStanding.Guest;

        if (membership is not null)
            return ParticipantStanding.Departed;

        // A claimed guest's stand-in is nobody: the person is a member under their own
        // account, and an old rule version still naming the stand-in is not a departure.
        // Asked of the history, the same way the past-members listing leaves them out, so
        // the two agree.
        if (await context.Set<GroupMembershipEvent>().AnyAsync(change =>
                change.GroupId == groupId && change.UserId == userId &&
                change.Kind == MembershipEventKind.GuestClaimed, ct))
            return ParticipantStanding.Stranger;

        if (await HasTraceIn(groupId, userId, ct))
            return ParticipantStanding.Departed;

        return ParticipantStanding.Stranger;
    }

    public async Task<IReadOnlyList<ParticipantPosition>> DepartedOn(Transaction transaction,
        CancellationToken ct = default)
    {
        if (transaction.GroupId is not { } groupId)
            return [];

        var current = await IdsOf(groupId, ct);

        return transaction.Splits.Select(split => split.UserId)
            .Append(transaction.Payer)
            .Distinct()
            .Where(userId => !current.Contains(userId))
            .Select(userId => ParticipantPosition.Of(transaction, userId, groupId))
            .ToList();
    }

    /// <summary>
    /// Whether the group's ledger or rules still name somebody: the same four places the
    /// past-members listing reads.
    /// </summary>
    /// <remarks>
    /// One query, four EXISTS: asked of the group row so the database answers the whole
    /// question in a single round trip rather than four in a row.
    /// </remarks>
    private Task<bool> HasTraceIn(Guid groupId, Guid userId, CancellationToken ct) =>
        context.Set<Group>()
            .Where(group => group.Id == groupId)
            .AnyAsync(_ =>
                context.Set<Transaction>().Any(transaction =>
                    transaction.GroupId == groupId && transaction.UserId == userId) ||
                context.Set<TransactionSplit>().Any(split =>
                    split.Transaction.GroupId == groupId && split.UserId == userId) ||
                context.Set<SplitRuleParticipant>().Any(participant =>
                    participant.SplitRuleVersion.SplitRule.Group.Id == groupId && participant.UserId == userId) ||
                context.Set<SoleSplitRuleVersion>().Any(version =>
                    version.SplitRule.Group.Id == groupId && version.UserId == userId), ct);

    public User StandInFor(string name)
    {
        // The name goes in FirstName, which is where every listing already looks. It is the
        // whole of what the group has told us, and splitting a typed-in "Ana Ruiz" into two
        // columns would be the app deciding which half is the surname.
        var standIn = new User { FirstName = name, Kind = UserKind.StandIn };

        context.Add(standIn);

        return standIn;
    }

    public async Task<ParticipantHandover> HandOver(Guid groupId, Guid fromUserId, Guid toUserId,
        CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
            return ParticipantHandover.Nothing;

        // First, and found before anything moves: a repayment between the two of them is the
        // one row the hand-over cannot simply repoint, because it would end up naming the
        // claimer at both ends. A transfer has one payer and one share and never names the
        // same person twice, so these are exactly the ones with one of the two at each end.
        var between = await context.Set<Transfer>()
            .Include(transfer => transfer.Splits)
            .Where(transfer => transfer.GroupId == groupId &&
                               ((transfer.UserId == fromUserId &&
                                 transfer.Splits.Any(split => split.UserId == toUserId)) ||
                                (transfer.UserId == toUserId &&
                                 transfer.Splits.Any(split => split.UserId == fromUserId))))
            .ToListAsync(ct);

        var cancelled = between.Select(transfer => transfer.Id).ToList();

        context.RemoveRange(between.SelectMany(transfer => transfer.Splits));
        context.RemoveRange(between);

        var shares = await context.Set<TransactionSplit>()
            .Where(split => split.Transaction.GroupId == groupId && split.UserId == fromUserId &&
                            !cancelled.Contains(split.TransactionId))
            .ToListAsync(ct);

        // The receiver's own shares on the same transactions, so a transaction that names
        // them both ends up with one row rather than two.
        var transactionIds = shares.Select(split => split.TransactionId).ToList();

        var theirs = await context.Set<TransactionSplit>()
            .Where(split => transactionIds.Contains(split.TransactionId) && split.UserId == toUserId)
            .ToDictionaryAsync(split => split.TransactionId, ct);

        var amountOwed = 0m;

        foreach (var share in shares)
        {
            amountOwed += share.Amount;

            if (theirs.TryGetValue(share.TransactionId, out var mine))
            {
                mine.Amount += share.Amount;
                context.Remove(share);
            }
            else
            {
                share.UserId = toUserId;
            }
        }

        var paid = await context.Set<Transaction>()
            .Where(transaction => transaction.GroupId == groupId && transaction.UserId == fromUserId &&
                                  !cancelled.Contains(transaction.Id))
            .ToListAsync(ct);

        var amountPaid = 0m;

        foreach (var transaction in paid)
        {
            amountPaid += transaction.Amount;
            transaction.UserId = toUserId;
        }

        // The rule the group holds for them first, because it is the one rule that is
        // theirs rather than one that merely names them: it becomes the receiver's, or is
        // retired if the receiver already has one. The revisions below skip provisioned
        // rules, so nothing built in reaches them.
        await memberRules.HandOver(groupId, fromUserId, toUserId, ct);

        // The rules that name them follow them, weight and all, and a rule that put the whole
        // amount on them now puts it on the claimer.
        var rewrite = await revisions.WithoutParticipant(groupId, fromUserId, toUserId,
            RuleHandling.Transfer, ct);

        return new ParticipantHandover(
            shares.Count, amountOwed, paid.Count, amountPaid, rewrite.Rules, rewrite.Emptied,
            between.Count, between.Sum(transfer => transfer.Amount));
    }
}

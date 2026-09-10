using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// What one hand-over moved, so whoever asked for it can be told.
/// </summary>
public record ParticipantHandover(
    int SharesMoved,
    decimal AmountOwed,
    int PaymentsMoved,
    decimal AmountPaid,
    int RulesAffected)
{
    public static readonly ParticipantHandover Nothing = new(0, 0m, 0, 0m, 0);

    public bool MovedAnything => SharesMoved > 0 || PaymentsMoved > 0 || RulesAffected > 0;
}

/// <summary>
/// Who a group may point at when it records money: its members, and the people it has
/// invited and is still waiting on.
/// </summary>
/// <remarks>
/// The distinction this exists to keep straight. <em>Membership</em> is who may read and
/// change the group -- every authorization check in the app asks that question of
/// <c>Group.Users</c> and must go on asking it of nothing else. <em>Participation</em> is
/// who may be given a share, which is a wider set, because spending does not wait for
/// people to answer their invitations: the flat is moved into and the trip is booked before
/// everybody has signed up.
/// <para>
/// So a pending invitee is choosable wherever a person is chosen for money -- a
/// transaction's payer, a stated split, a split rule -- and choosable nowhere else. They
/// are not in the group, cannot see it, and there is nobody to settle up with; what they
/// have is a position in its balances, waiting for them.
/// </para>
/// </remarks>
public interface IGroupParticipants
{
    /// <summary>
    /// Everyone a group may record money against, members and pending invitees alike.
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
    /// The wire shape of a group's people, each row saying whether they have joined.
    /// </summary>
    /// <remarks>
    /// Takes the query rather than the group's id because the scoping is the caller's --
    /// <c>GetGroupMembers</c> answers with nothing at all for a group the reader is not in --
    /// and only the flag belongs here. It is a fact about the pair and not about the person:
    /// the same account can be a member of one group and an unanswered invitation in another,
    /// so the group being read has to be said.
    /// </remarks>
    IQueryable<UserInfo> Describe(IQueryable<User> people, Guid groupId);

    /// <summary>
    /// The participant of <paramref name="groupId"/> with that id, or null when they are
    /// neither a member nor invited.
    /// </summary>
    Task<User?> Find(Guid groupId, Guid userId, CancellationToken ct = default);

    /// <summary>Whether that person has been invited to the group and has not joined it.</summary>
    Task<bool> IsPendingInvitee(Guid groupId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// A new stand-in for a person a group has named: a row with that name on it, no
    /// address, and no identity, so nobody can sign in as it.
    /// </summary>
    /// <remarks>
    /// It exists to be given shares before there is an account to give them to. Claiming
    /// the invitation is what puts a real person behind it, and claiming moves the position
    /// onto that person's own account and deletes this -- so a stand-in is never a lasting
    /// second row for somebody who is already here.
    /// <para>
    /// Always new, and never an existing account: there is no address to recognise one by,
    /// and guessing from a name would be handing a stranger's ledger to whoever the group
    /// happened to call Dani.
    /// </para>
    /// <para>
    /// Added to the context and not saved. The caller is writing the invitation in the same
    /// unit of work, and a stand-in with no invitation behind it is a row nothing would
    /// ever point at.
    /// </para>
    /// </remarks>
    User StandInFor(string name);

    /// <summary>
    /// Who takes over what an invitation was holding, when it is declined or withdrawn: the
    /// member who sent it, or -- if they have gone -- the group's longest-standing member.
    /// </summary>
    /// <remarks>
    /// Not a choice the caller makes, and deliberately the same rule on both sides. The
    /// group is present when it withdraws one and could be asked; the invitee declining is
    /// not in the group and could not be asked at all, and an outcome that depended on who
    /// pressed the button would be two different rules for one event.
    /// <para>
    /// Null only for a group with no members left, which
    /// <c>GROUP_CANNOT_LEAVE_LAST_MEMBER</c> makes unreachable.
    /// </para>
    /// </remarks>
    Task<User?> Absorber(GroupInvitation invitation, CancellationToken ct = default);

    /// <summary>
    /// Hands everything one participant holds in a group to another: their shares, the
    /// transactions they were down as having paid for, and their place in the group's split
    /// rules.
    /// </summary>
    /// <remarks>
    /// Amounts are never touched. A share moves from one name to another, and where the
    /// receiver already had a share on the same transaction the two are added into one row
    /// -- because a transaction may hold only one opinion about what a person owed. So
    /// every transaction still sums to its own amount, which is the invariant the group's
    /// balances rest on, and the group's balances still sum to zero.
    /// <para>
    /// Rules are the exception, and are pruned rather than merged: a rule is a template for
    /// the next expense rather than a record of anything, and weights are proportional, so
    /// dropping the name divides what was theirs among the rest. Exactly what happens when
    /// a member leaves -- see <c>GroupService.DetachMember</c>.
    /// </para>
    /// <para>
    /// Does not save, so the caller can apply it together with whatever else the same
    /// action changes -- an invitation being removed, an account being anonymised.
    /// </para>
    /// </remarks>
    Task<ParticipantHandover> HandOver(Guid groupId, Guid fromUserId, Guid toUserId,
        CancellationToken ct = default);
}

public sealed class GroupParticipants(AppDbContext context) : IGroupParticipants
{
    public IQueryable<User> Of(Guid groupId) =>
        context.Set<User>()
            .Where(user => user.Groups.Any(@group => @group.Id == groupId) ||
                           context.Set<GroupInvitation>().Any(invitation =>
                               invitation.GroupId == groupId &&
                               invitation.ParticipantUserId == user.Id));

    public async Task<IReadOnlyCollection<Guid>> IdsOf(Guid groupId, CancellationToken ct = default) =>
        await Of(groupId).Select(user => user.Id).ToListAsync(ct);

    public IQueryable<UserInfo> Describe(IQueryable<User> people, Guid groupId) =>
        from user in people
        select new UserInfo(user.Id, user.FirstName, user.LastName, user.Email,
            // A participant who is not a member is one of the invited: that is what the
            // listing is the union of.
            !user.Groups.Any(@group => @group.Id == groupId));

    public Task<User?> Find(Guid groupId, Guid userId, CancellationToken ct = default) =>
        Of(groupId).FirstOrDefaultAsync(user => user.Id == userId, ct);

    /// <remarks>
    /// No membership check beside it, because the two sets cannot overlap: an invitation's
    /// participant is a stand-in the group made, nobody can sign in as one, and claiming the
    /// invitation deletes it. So being named by a pending invitation is the whole of the
    /// question.
    /// </remarks>
    public Task<bool> IsPendingInvitee(Guid groupId, Guid userId, CancellationToken ct = default) =>
        context.Set<GroupInvitation>()
            .AnyAsync(invitation => invitation.GroupId == groupId &&
                                    invitation.ParticipantUserId == userId, ct);

    public User StandInFor(string name)
    {
        // The name goes in FirstName, which is where every listing already looks. It is the
        // whole of what the group has told us, and splitting a typed-in "Ana Ruiz" into two
        // columns would be the app deciding which half is the surname.
        var standIn = new User { FirstName = name };

        context.Add(standIn);

        return standIn;
    }

    public async Task<User?> Absorber(GroupInvitation invitation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (invitation.InvitedByUserId is { } inviterId)
        {
            var inviter = await context.Set<User>()
                .FirstOrDefaultAsync(user => user.Id == inviterId &&
                                             user.Groups.Any(@group => @group.Id == invitation.GroupId), ct);

            if (inviter is not null)
                return inviter;
        }

        // Longest-standing member. Deterministic, and the person likeliest to recognise
        // what the money was: the group's own creator, in the ordinary case where nobody
        // has left.
        return await (from membership in context.Set<GroupMembership>()
                      join user in context.Set<User>() on membership.UserId equals user.Id
                      where membership.GroupId == invitation.GroupId
                      orderby membership.JoinedAt, membership.UserId
                      select user)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ParticipantHandover> HandOver(Guid groupId, Guid fromUserId, Guid toUserId,
        CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
            return ParticipantHandover.Nothing;

        var shares = await context.Set<TransactionSplit>()
            .Where(split => split.Transaction.GroupId == groupId && split.UserId == fromUserId)
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
            .Where(transaction => transaction.GroupId == groupId && transaction.UserId == fromUserId)
            .ToListAsync(ct);

        var amountPaid = 0m;

        foreach (var transaction in paid)
        {
            amountPaid += transaction.Amount;
            transaction.UserId = toUserId;
        }

        var named = await context.Set<SplitRuleParticipant>()
            .Where(participant => participant.SplitRule.Group.Id == groupId &&
                                  participant.UserId == fromUserId)
            .ToListAsync(ct);

        context.RemoveRange(named);

        return new ParticipantHandover(shares.Count, amountOwed, paid.Count, amountPaid, named.Count);
    }
}

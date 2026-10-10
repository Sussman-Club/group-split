using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Services;

/// <summary>What a call to <see cref="IGroupJoiner.Join"/> found and did.</summary>
public enum JoinOutcome
{
    /// <summary>They had never been in the group, and are now.</summary>
    Joined,

    /// <summary>They had been in it and left, and this brought them back.</summary>
    Rejoined,

    /// <summary>
    /// They were already in it, which is an answer and not a failure: following the same
    /// link twice, or claiming an invitation to a group somebody has since joined another
    /// way, both land here.
    /// </summary>
    AlreadyAMember
}

/// <summary>
/// Putting somebody into a group, which is the one thing accepting an invitation and
/// following a join link both come down to.
/// </summary>
/// <remarks>
/// One implementation so the two ways in cannot drift: a member joined by link is a member
/// joined by invitation in every respect, including the moment the join is stamped with.
/// There is no role to grant either way -- a group has members and nothing else -- so "an
/// ordinary member" is not a decision made here, it is the only thing there is to be.
/// <para>
/// It is also the one place that decides whether somebody who has left may come back. Both
/// ways in carry the moment they were issued, and a departure newer than that moment makes
/// them useless to that person: a link that was shared while they were in the group was
/// never an invitation to return.
/// </para>
/// </remarks>
public interface IGroupJoiner
{
    /// <summary>
    /// Puts <paramref name="user"/> into <paramref name="group"/>, or back into it, and
    /// gives them the rule every member has.
    /// </summary>
    /// <param name="issuedAt">
    /// When the link or invitation being used was issued. Somebody whose membership ended at
    /// or after this moment is refused with <c>GROUP_REJOIN_NEEDS_NEW_INVITATION</c>.
    /// </param>
    Task<JoinOutcome> Join(Group group, User user, DateTimeOffset issuedAt, CancellationToken ct = default);

    /// <summary>
    /// Refuses, with <c>GROUP_REJOIN_NEEDS_NEW_INVITATION</c>, somebody whose membership of
    /// the group ended at or after <paramref name="issuedAt"/>. Does nothing for anybody
    /// else.
    /// </summary>
    /// <remarks>
    /// For the landing pages, which describe a link before anybody presses anything: saying
    /// "this link will not let you back in" there is better than saying it after the button.
    /// </remarks>
    Task EnsureMayRejoin(Guid groupId, Guid userId, DateTimeOffset issuedAt, CancellationToken ct = default);
}

public sealed class GroupJoiner(AppDbContext context, IMemberSplitRules memberRules) : IGroupJoiner
{
    public async Task<JoinOutcome> Join(Group group, User user, DateTimeOffset issuedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(user);

        // Only somebody who signs in becomes a member. A stand-in holds a position until it
        // is claimed and a deleted account holds nothing; either one here is a bug in the
        // caller, and a membership row for it would be one nobody could ever use or leave.
        if (user.Kind is not UserKind.Account)
            throw new InvalidOperationException(
                $"Only an account can join a group, and user {user.Id} is a {user.Kind}.");

        // Nothing here answers an invitation, and there is nothing for it to answer. An
        // invitation names a person the group invented and holds a stand-in of its own, so a
        // signed-in account is never the participant of a pending one -- the way in through
        // an invitation is IInvitationService.Claim, which hands that stand-in's position
        // over and then deletes both.
        //
        // The row decides, with the filter off: a closed membership is exactly what this has
        // to see. Not group.Users, because reading the row puts a departed person back into
        // that collection in memory.
        var membership = await Row(group.Id, user.Id, ct);

        if (membership is { LeftAt: null })
            return JoinOutcome.AlreadyAMember;

        if (membership is { LeftAt: { } leftAt } && leftAt >= issuedAt)
            throw Refusal(membership, issuedAt);

        var now = DateTimeOffset.UtcNow;
        JoinOutcome outcome;

        if (membership is not null)
        {
            // The same row, opened again. A group has one relationship with each person, and
            // the rejoin rule only ever needs the latest departure.
            membership.LeftAt = null;
            membership.LeftReason = null;
            membership.LeftByUserId = null;
            membership.JoinedAt = now;

            // Rejoining is opting back in. A group they come back to and find filed away at
            // the bottom of their own list reads as a join that did not work.
            membership.ArchivedAt = null;

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The same race as the insert below, for somebody coming back: the other
                // follow of the link reopened the row first, and LeftAt no longer says what
                // this one read. They are in, which is what both asked for.
                context.Entry(membership).State = EntityState.Detached;
                return JoinOutcome.AlreadyAMember;
            }

            outcome = JoinOutcome.Rejoined;
        }
        else
        {
            // Written as a row rather than by adding to group.Users, so the moment is in the
            // insert itself instead of stamped by a second save afterwards.
            membership = new GroupMembership { GroupId = group.Id, UserId = user.Id, JoinedAt = now };
            context.Add(membership);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException
                      {
                          SqlState: PostgresErrorCodes.UniqueViolation,
                          ConstraintName: "PK_GroupUser"
                      })
            {
                // Two follows of one link in the same instant: the other request's insert is
                // what put them there, and following a link twice is not an error. Only the
                // failed row is dropped, the same recovery EnsureFor and Remember make.
                context.Entry(membership).State = EntityState.Detached;
                return JoinOutcome.AlreadyAMember;
            }

            outcome = JoinOutcome.Joined;
        }

        // EF fixes the skip navigations up from a tracked join row with both keys set, but
        // the responses count group.Users, so this does not lean on it.
        if (group.Users.All(member => member.Id != user.Id))
            group.Users.Add(user);

        // And the rule that says an expense is all theirs, which every member has and
        // nobody writes. Here rather than beside each way in, for the reason this class
        // exists: a member joined by link must be a member joined by invitation in every
        // respect.
        await memberRules.EnsureFor(group, user, ct);

        return outcome;
    }

    public async Task EnsureMayRejoin(Guid groupId, Guid userId, DateTimeOffset issuedAt,
        CancellationToken ct = default)
    {
        var membership = await Row(groupId, userId, ct);

        if (membership is { LeftAt: { } leftAt } && leftAt >= issuedAt)
            throw Refusal(membership, issuedAt);
    }

    /// <summary>The membership row for the pair, open or closed.</summary>
    private Task<GroupMembership?> Row(Guid groupId, Guid userId, CancellationToken ct) =>
        context.Set<GroupMembership>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(row => row.GroupId == groupId && row.UserId == userId, ct);

    private static DomainException Refusal(GroupMembership membership, DateTimeOffset issuedAt) =>
        new ConflictException(ErrorCodes.GroupRejoinNeedsNewInvitation,
                (membership.LeftReason is LeftReason.Removed
                    ? "You were removed from this group"
                    : "You left this group")
                + " after this link was issued, so it cannot bring you back. "
                + "Ask someone in the group for a new one.")
            .WithExtension("leftAt", membership.LeftAt)
            .WithExtension("leftReason", membership.LeftReason?.ToString())
            .WithExtension("issuedAt", issuedAt);
}

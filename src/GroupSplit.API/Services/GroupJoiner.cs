using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Putting somebody into a group, which is the one thing accepting an invitation and
/// following a join link both come down to.
/// </summary>
/// <remarks>
/// One implementation so the two ways in cannot drift: a member joined by link is a member
/// joined by invitation in every respect, including the moment the join is stamped with.
/// There is no role to grant either way -- a group has members and nothing else -- so "an
/// ordinary member" is not a decision made here, it is the only thing there is to be.
/// </remarks>
public interface IGroupJoiner
{
    /// <summary>
    /// Adds <paramref name="user"/> to <paramref name="group"/> if they are not in it
    /// already. <paramref name="group"/> must have been loaded with its members.
    /// </summary>
    /// <returns>
    /// Whether this call is what put them there. False means they were already a member,
    /// which is an answer and not a failure: following the same link twice, or accepting an
    /// invitation to a group somebody has since joined another way, both land here.
    /// </returns>
    Task<bool> Join(Group group, User user, CancellationToken ct = default);
}

public sealed class GroupJoiner(AppDbContext context) : IGroupJoiner
{
    public async Task<bool> Join(Group group, User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(user);

        if (group.Users.Any(member => member.Id == user.Id))
            return false;

        group.Users.Add(user);
        await context.SaveChangesAsync(ct);

        // After the join, and separately: EF writes the join row itself, so the payload on
        // it is set on the row that now exists rather than on one built by hand.
        var membership = await context.Set<GroupMembership>()
            .FirstOrDefaultAsync(row => row.GroupId == group.Id && row.UserId == user.Id, ct);

        if (membership is not null)
        {
            membership.JoinedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(ct);
        }

        return true;
    }
}

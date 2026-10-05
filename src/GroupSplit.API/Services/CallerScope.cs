using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Two things the services that write on a caller's behalf all need: the group, if the caller
/// is in it, and a way to start a retried transaction from what the database holds.
/// </summary>
internal static class CallerScope
{
    extension(AppDbContext context)
    {
        /// <summary>
        /// The group, when <paramref name="userId"/> is a current member of it; otherwise
        /// 404 <c>GROUP_NOT_FOUND</c>, the same answer a group that does not exist gets.
        /// </summary>
        public async Task<Group> GroupOfMember(Guid groupId, Guid userId, CancellationToken ct = default) =>
            await context.Set<Group>()
                .FirstOrDefaultAsync(group =>
                    group.Id == groupId && group.Users.Any(user => user.Id == userId), ct)
            ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        /// <summary>
        /// Starts an attempt at a multi-save transaction from the database rather than from
        /// whatever the last attempt left tracked.
        /// </summary>
        /// <remarks>
        /// The deployed provider retries a transaction that hit a transient failure by
        /// running the whole delegate again on the same context. Every save inside the first
        /// attempt marked its changes as stored, and the rollback did not unmark them, so the
        /// second attempt would read tracked rows that claim writes the database no longer
        /// has. Clearing first makes an attempt mean the same thing whichever one it is. The
        /// signed-in account is put back, because the services hold on to it and it is the
        /// one row every write refers to -- that one row, and nothing reachable from it.
        /// <para>
        /// Only before a retry. The first attempt has no earlier one to forget, and clearing
        /// then would throw away anything the request had tracked and not yet saved --
        /// changes that are nobody's to discard there.
        /// </para>
        /// </remarks>
        public void StartOver(User signedIn)
        {
            context.ChangeTracker.Clear();
            context.Entry(signedIn).State = EntityState.Unchanged;
        }
    }
}

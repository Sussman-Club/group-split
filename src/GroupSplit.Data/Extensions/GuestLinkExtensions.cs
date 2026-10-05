using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data.Extensions;

/// <summary>
/// Ending a guest's link: the one change every path that touches a link has in common.
/// </summary>
public static class GuestLinkExtensions
{
    extension(AppDbContext context)
    {
        /// <summary>
        /// Stops <paramref name="guest"/>'s link, if it has one: clears it, and forgets who
        /// had opened it. Does not save.
        /// </summary>
        /// <remarks>
        /// The forgetting is the half that is easy to miss. Opening a link is remembered so
        /// that the person can find it again from their own list -- and that list is read
        /// through the guest, not the token. Left behind, those rows would hand whoever
        /// opened a link the guest's next one: replacing a link that leaked would give the
        /// replacement to the person it leaked to. So every way a link ends -- replaced,
        /// stopped, turned down, claimed, or the guest removed -- comes through here.
        /// </remarks>
        public async Task EndLink(GroupGuest guest, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(guest);

            guest.Token = null;
            guest.LinkIssuedAt = null;
            guest.LinkIssuedByUserId = null;
            guest.LinkIssuedBy = null;

            var opened = await context.Set<InvitationOpened>()
                .Where(row => row.GuestId == guest.Id)
                .ToListAsync(ct);

            context.RemoveRange(opened);
        }
    }
}

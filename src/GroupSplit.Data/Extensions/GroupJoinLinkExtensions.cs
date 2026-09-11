using GroupSplit.Data.Entities;

namespace GroupSplit.Data.Extensions;

/// <summary>
/// Reading a join link: whether it still opens the door.
/// </summary>
/// <remarks>
/// Beside <see cref="GroupJoinLink"/> rather than on it, because the entities here hold
/// data and answer no questions about it.
/// </remarks>
public static class GroupJoinLinkExtensions
{
    extension(GroupJoinLink link)
    {
        /// <summary>
        /// Whether it will still let somebody in at <paramref name="now"/>.
        /// </summary>
        /// <remarks>
        /// Two ways to be closed and they are not the same thing to the person holding the
        /// link -- withdrawn by the group, or simply out of time -- but they are the same
        /// answer to the question of whether it works, which is what this is for. Which of
        /// the two it was is <see cref="GroupJoinLink.RevokedAt"/>'s to say, and the service
        /// says it, because "that link was withdrawn" and "that link has expired" are
        /// different things to tell somebody.
        /// <para>
        /// The instant is passed in rather than read from the clock, so that the decision is
        /// the caller's and a test can make a link expire without waiting a fortnight.
        /// </para>
        /// </remarks>
        public bool IsActive(DateTimeOffset now) => link.RevokedAt is null && link.ExpiresAt > now;
    }
}

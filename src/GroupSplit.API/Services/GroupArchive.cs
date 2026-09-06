using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// What being archived means, in the one place every write asks about it.
/// </summary>
/// <remarks>
/// A group is archived so it can be put down without being lost: the trip is over, the
/// flat share ended, and nobody should be adding to it -- but the balances and the history
/// are still worth reading, and someone may want it back. So the refusal is on the writes
/// and never on the reads, and it is reversible by any member who archived it or any other.
/// <para>
/// Deliberately one refusal for all of them. A member who cannot rename the group for this
/// reason also cannot add an expense to it for this reason, and telling them so eleven
/// different ways would only make the same fact harder to recognise.
/// </para>
/// </remarks>
public static class GroupArchive
{
    public static ConflictException Refusal() =>
        new(ErrorCodes.GroupArchived,
            "The group is archived and does not accept changes. Unarchive it first.");

    extension(Group group)
    {
        public bool IsArchived => group.ArchivedAt is not null;

        /// <summary>Throws when the group is archived. Call after the group is known to exist.</summary>
        public void EnsureNotArchived()
        {
            if (group.ArchivedAt is not null)
                throw Refusal();
        }
    }
}

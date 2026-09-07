namespace GroupSplit.Data.Entities;

/// <summary>
/// One person's membership of one group, and what is true of it for them alone.
/// </summary>
/// <remarks>
/// The join between <see cref="User"/> and <see cref="Group"/> used to be one EF made for
/// itself, which is fine until the pair has something of its own to carry.
/// <see cref="ArchivedAt"/> is the first such thing: archiving is personal, like archiving
/// a note, so it cannot live on the group -- one member tidying their list would otherwise
/// tidy everyone's.
/// <para>
/// Mapped onto the table and columns EF had already made, so making it explicit adds a
/// column rather than moving anyone's membership.
/// </para>
/// </remarks>
public class GroupMembership
{
    public Guid GroupId { get; init; }

    public Guid UserId { get; init; }

    /// <summary>
    /// When they joined.
    /// </summary>
    /// <remarks>
    /// Not nullable, even though every row that existed before the column did. There are
    /// few enough of those to give them a date outright -- the migration stamps them with
    /// the day it ran -- and a nullable column would have made every reader downstream
    /// carry a case that only ever meant "early".
    /// <para>
    /// The database fills it, because EF writes this join row itself when somebody is added
    /// to <see cref="Group.Users"/> and there is no constructor to run. The services that
    /// know the moment set it straight after, which is also what keeps it right on the
    /// in-memory provider the tests use, where a store default does not apply.
    /// </para>
    /// </remarks>
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>
    /// When this member archived the group, or null while they have not. Nothing about the
    /// group changes: it accepts every write it would otherwise accept, and the other
    /// members never see this. It only moves down their own list.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }
}

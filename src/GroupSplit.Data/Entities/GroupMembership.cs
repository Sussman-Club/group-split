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
/// <para>
/// A row outlives the membership it records. Leaving, being removed and deleting an account
/// close it with <see cref="LeftAt"/> rather than deleting it, because a group has to
/// remember that somebody left in order to refuse them a link that was shared while they
/// were still in it. A model-wide query filter keeps closed rows out of
/// <c>Group.Users</c> and <c>User.Groups</c>, so everything that asks "is this person a
/// member" goes on meaning "a current member" without saying so.
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

    /// <summary>
    /// When the membership ended, or null while it stands. A row with this set is history:
    /// the person is not a member, and the query filter hides the row from every read that
    /// does not ask for it with <c>IgnoreQueryFilters</c>.
    /// </summary>
    /// <remarks>
    /// Cleared again when the same person rejoins, which reuses the row rather than adding a
    /// second: the key is the pair, and this row is the relationship as it stands. Earlier
    /// stints are not lost with it -- every join, departure and return is also written to
    /// <see cref="GroupMembershipEvent"/>, which is where "who removed whom, and when" is
    /// answered. The rejoin rule only needs the latest departure, and reads it here.
    /// </remarks>
    public DateTimeOffset? LeftAt { get; set; }

    /// <summary>How the membership ended. Null exactly when <see cref="LeftAt"/> is.</summary>
    public LeftReason? LeftReason { get; set; }

    /// <summary>
    /// Who ended it: the member who removed them, or the person themselves when they left or
    /// deleted their account. Null while the membership stands, and once that account is
    /// gone.
    /// </summary>
    public Guid? LeftByUserId { get; set; }
}

/// <summary>How somebody stopped being a member of a group.</summary>
/// <remarks>
/// All three leave the same standing behind -- the person is no longer in the group -- and
/// differ only in who acted. The difference is kept because it is what a refusal has to say:
/// "you left" and "you were removed" are different things to be told.
/// </remarks>
public enum LeftReason
{
    /// <summary>They left on their own.</summary>
    Left,

    /// <summary>Another member removed them.</summary>
    Removed,

    /// <summary>They deleted their account.</summary>
    AccountDeleted
}

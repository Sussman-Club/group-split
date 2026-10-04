namespace GroupSplit.Data.Entities;

/// <summary>
/// One thing that happened to somebody's membership of a group: they joined, left, were
/// removed, deleted their account, or came back.
/// </summary>
/// <remarks>
/// <see cref="GroupMembership"/> is the relationship as it stands, one row per person, and a
/// return reopens that row -- which would otherwise wipe the departure before it. This keeps
/// the whole sequence, so a group that removed somebody in March and took them back in May
/// still knows both, and who did each.
/// <para>
/// Written by <c>AppDbContext</c> as part of saving the membership change itself, never by
/// hand: every path that moves a membership -- creating a group, joining by link or
/// invitation, leaving, removal, account deletion, the seeder -- goes through a save, so
/// none of them can forget to record it. Append-only; nothing updates or deletes these
/// except the group itself going.
/// </para>
/// </remarks>
public class GroupMembershipEvent : Entity
{
    public Guid GroupId { get; set; }

    /// <summary>Whose membership this is about.</summary>
    public Guid UserId { get; set; }

    public MembershipEventKind Kind { get; set; }

    /// <summary>When it happened: the join time for a join, the departure time for a departure.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Who did it: the person themselves for a join, a return, leaving or deleting their
    /// account, and the remover for a removal. Null once that account is gone.
    /// </summary>
    public Guid? ByUserId { get; set; }
}

/// <summary>What happened to a membership.</summary>
/// <remarks>
/// The departures mirror <see cref="LeftReason"/> one for one, and keep their names so the
/// two read the same in the database.
/// </remarks>
public enum MembershipEventKind
{
    /// <summary>Became a member for the first time: created the group, or joined it.</summary>
    Joined,

    /// <summary>Became a member again after having left or been removed.</summary>
    Rejoined,

    /// <summary>Left on their own.</summary>
    Left,

    /// <summary>Removed by another member.</summary>
    Removed,

    /// <summary>Stopped being a member by deleting their account.</summary>
    AccountDeleted
}

namespace GroupSplit.Data.Entities;

/// <summary>
/// Somebody has been asked to join a group and has not yet answered.
/// </summary>
/// <remarks>
/// A table of its own rather than a status on <see cref="GroupMembership"/>, which is where
/// the plan first put it. A membership is keyed on (group, user), so an invitation modelled
/// there would need a null user in half a primary key -- and the whole reason invitations
/// exist is the address that has no account behind it yet. Adding somebody by email used to
/// look up the address and silently drop it when nothing matched, so inviting a friend who
/// had not signed up yet succeeded and did nothing.
/// <para>
/// It is a pending thing only. Answering it -- accepted, declined, or withdrawn by the
/// group -- removes the row, and joining is the membership that replaces it. Nothing reads
/// a history of invitations, and a kept row with a status on it would have to be excluded
/// from every query that asks who is waiting.
/// </para>
/// </remarks>
public class GroupInvitation : Entity
{
    public virtual Group Group { get; set; } = null!;

    public Guid GroupId { get; set; }

    /// <summary>
    /// Who was asked, lower-cased, because that is the only handle there is: the address
    /// may belong to an account, to an account made later, or to nobody at all.
    /// </summary>
    public required string Email { get; set; }

    /// <summary>
    /// Who asked. Nullable so that an account deleting itself does not take the
    /// invitations it sent with it -- the group still means them.
    /// </summary>
    public virtual User? InvitedBy { get; set; }

    public Guid? InvitedByUserId { get; set; }

    public required DateTimeOffset InvitedAt { get; set; }
}

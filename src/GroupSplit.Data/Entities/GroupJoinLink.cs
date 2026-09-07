namespace GroupSplit.Data.Entities;

/// <summary>
/// A link that lets whoever holds it join one group.
/// </summary>
/// <remarks>
/// The second way into a group, alongside <see cref="GroupInvitation"/>, and the one people
/// actually use. An invitation is keyed on an address and is only discoverable by signing
/// in and looking, so somebody invited before they have an account has no way to know --
/// which is the case invitations were added for. A link goes into the thread where the
/// group already talks, and whoever is in that thread can join. Reaching an invitee stops
/// depending on a mail relay.
/// <para>
/// <see cref="Token"/> is a bearer credential: holding it is the whole of the
/// authorisation. So it names exactly one group, grants nothing but ordinary membership of
/// it, and expires on its own. It is stored as it is issued rather than hashed, because a
/// member has to be able to open the group and copy the same link again a week later --
/// hashing it would mean a link could only be read at the moment it was made, which is not
/// how somebody shares one.
/// </para>
/// <para>
/// Revoking sets <see cref="RevokedAt"/> and keeps the row, which is the opposite of what
/// an invitation does. An answered invitation is gone because nothing asks after it again;
/// a revoked link is asked after every time somebody opens the URL that is still sitting in
/// a chat thread, and a deleted row could only answer "no such link". The row is what lets
/// the app say the link was withdrawn rather than that it never existed.
/// </para>
/// </remarks>
public class GroupJoinLink : Entity
{
    /// <summary>How long a new link is good for.</summary>
    /// <remarks>
    /// Fixed rather than chosen. Choosing is a field on a dialog that nobody has an opinion
    /// about, and a link that has run out is one press to replace.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public virtual Group Group { get; set; } = null!;

    public Guid GroupId { get; set; }

    /// <summary>
    /// The random part of the URL, and the only thing that authorises the join.
    /// </summary>
    public required string Token { get; set; }

    /// <summary>
    /// Who made it. Nullable for the same reason an invitation's inviter is: an account
    /// deleting itself must not take a link the group is still sharing with it.
    /// </summary>
    public virtual User? CreatedBy { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When it was withdrawn, or null while it still stands.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Whether it will still let somebody in at <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

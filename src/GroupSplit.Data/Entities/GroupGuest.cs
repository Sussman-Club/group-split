namespace GroupSplit.Data.Entities;

/// <summary>
/// Somebody the group shares costs with who has no account in it: a name the group chose,
/// and the stand-in every share of theirs is recorded against.
/// </summary>
/// <remarks>
/// A table of its own rather than a status on <see cref="GroupMembership"/>. A membership is
/// keyed on (group, user) and means somebody who may read and change the group; a guest can
/// do neither. Members record everything for them -- their expenses, their shares, what they
/// paid back -- and the group can settle with them and, once they are square, remove them.
/// <para>
/// It used to be an invitation: a row that existed only while the group waited for an
/// answer, with a link minted the moment it was made. Most of the people a group splits with
/// never sign up, so waiting was the wrong frame -- a guest is somebody the group keeps track
/// of, and a link (<see cref="Token"/>) is an optional way for the real person to take that
/// over. Hence the link columns are nullable, and a guest can have one issued, replaced or
/// stopped at any time without anything else about them changing.
/// </para>
/// <para>
/// The row ends rather than going: being removed, or being claimed by whoever held the link,
/// sets <see cref="EndedAt"/>, and a query filter keeps ended rows out of every read, the way
/// it keeps closed memberships out of <c>Group.Users</c>. So "is this person a guest" means a
/// current guest everywhere without each read having to say so, and a removed guest falls
/// into the same "departed" standing as a member who left -- their shares can no longer move,
/// and they show among the group's past participants.
/// </para>
/// <para>
/// <see cref="Token"/> and <see cref="EndedAt"/> are concurrency tokens. Every write that
/// matters to a guest -- claiming, removing, issuing or stopping a link -- changes one of
/// them, so two of those at the same moment cannot both win: the second update finds the row
/// no longer says what it read, writes nothing, and is answered as if the guest or the link
/// were already gone.
/// </para>
/// </remarks>
public class GroupGuest : Entity
{
    /// <summary>How long a link is good for, counted from <see cref="LinkIssuedAt"/>.</summary>
    /// <remarks>
    /// Fixed rather than chosen, like <see cref="GroupJoinLink.Lifetime"/>, and longer than
    /// that one: the person a guest's link is for has usually never heard of the app, and
    /// a month is how long "I'll sign up when I get home" takes. An unclaimed link sitting
    /// in an old chat, a screenshot or a browser history is what this closes -- a link that
    /// has run out is one press to issue again.
    /// <para>
    /// Not stored. The end of a link is always this long after it was issued, so it is
    /// worked out from <see cref="LinkIssuedAt"/> rather than kept in a column of its own
    /// that could disagree with it -- and shortening this shortens every link already out,
    /// which is what a change made for safety should do.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);

    public virtual Group Group { get; set; } = null!;

    public Guid GroupId { get; set; }

    /// <summary>
    /// What the group calls them. The only thing it knows, and what every listing shows:
    /// there is no profile to read a name out of.
    /// </summary>
    /// <remarks>
    /// Not unique within the group. Two people can be called Dani, and refusing the second
    /// would be the app telling a group it has misremembered its own friends.
    /// </remarks>
    public required string Name { get; set; }

    /// <summary>
    /// The person this guest is money-wise: the row every share recorded against them
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// A <see cref="User"/> and not a shape of its own, because a share has to name
    /// somebody and every balance in the app is a sum over <see cref="TransactionSplit"/>
    /// rows keyed on a user. It is a stand-in (<see cref="UserKind.StandIn"/>): a row with the
    /// given name on it, no address and no identity, so nobody can sign in as it.
    /// <para>
    /// One stand-in per guest, ever, and it outlives the guest: claiming moves everything it
    /// holds onto the claimer's account and leaves it empty, and removing leaves it holding
    /// the history it was given. Either way the membership history and the ledger go on
    /// naming it, so it is never deleted.
    /// </para>
    /// </remarks>
    public virtual User Participant { get; set; } = null!;

    public Guid ParticipantUserId { get; set; }

    /// <summary>
    /// Who added them. Nullable so that an account deleting itself does not take the
    /// guests it added with it -- the group still means them.
    /// </summary>
    public virtual User? AddedBy { get; set; }

    public Guid? AddedByUserId { get; set; }

    public required DateTimeOffset AddedAt { get; set; }

    /// <summary>
    /// The random part of the URL a member sends, when one has been issued: whoever opens it
    /// and claims it becomes this guest. Null for a guest nobody has sent a link.
    /// </summary>
    /// <remarks>
    /// A bearer credential, and a heavier one than <see cref="GroupJoinLink.Token"/>. That
    /// one lets somebody in as themselves, claiming nothing; this one hands over a position
    /// in the group's ledger, so forwarding it gives away somebody else's debts. Single use:
    /// claiming clears it, and so do replacing it, stopping it, turning it down and removing
    /// the guest. And it runs out <see cref="LinkLifetime"/> after it was issued, unclaimed
    /// or not: an expired link stays in this column until a member issues another, but
    /// nothing answers to it.
    /// </remarks>
    public string? Token { get; set; }

    /// <summary>When the current link was issued. Set exactly when <see cref="Token"/> is.</summary>
    /// <remarks>
    /// What the rejoin rule reads: a link issued before somebody left the group cannot bring
    /// them back, whenever the guest it belongs to was added. And what the link's expiry is
    /// counted from (<see cref="LinkLifetime"/>), so issuing a link again restarts both.
    /// </remarks>
    public DateTimeOffset? LinkIssuedAt { get; set; }

    /// <summary>
    /// The moment the current link stops working, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Not mapped: a read-only property, worked out from <see cref="LinkIssuedAt"/>. Queries
    /// compare <see cref="LinkIssuedAt"/> with a cutoff instead (<see cref="IssuedSince"/>),
    /// which every provider can translate.
    /// </remarks>
    public DateTimeOffset? LinkExpiresAt => LinkIssuedAt + LinkLifetime;

    /// <summary>
    /// The earliest a link can have been issued and still work at <paramref name="now"/>:
    /// a link works while <c>LinkIssuedAt &gt; IssuedSince(now)</c>, and from the instant it
    /// reaches its expiry it does not.
    /// </summary>
    public static DateTimeOffset IssuedSince(DateTimeOffset now) => now - LinkLifetime;

    /// <summary>Whether there is a link, and it has not run out at <paramref name="now"/>.</summary>
    public bool HasWorkingLink(DateTimeOffset now) =>
        Token is not null && LinkIssuedAt > IssuedSince(now);

    /// <summary>Who issued the current link, while it stands.</summary>
    public virtual User? LinkIssuedBy { get; set; }

    public Guid? LinkIssuedByUserId { get; set; }

    /// <summary>
    /// When they stopped being a guest, or null while they are one. Set together with
    /// <see cref="EndReason"/>.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    public GuestEndReason? EndReason { get; set; }

    /// <summary>
    /// Who ended it: the member who removed them, or the account that claimed them. Null
    /// while they are a guest, and once that account is gone.
    /// </summary>
    public Guid? EndedByUserId { get; set; }
}

/// <summary>Why somebody stopped being a guest.</summary>
public enum GuestEndReason
{
    /// <summary>A member removed them, once they were square with the group.</summary>
    Removed,

    /// <summary>Whoever held their link claimed it and became a member in their place.</summary>
    Claimed
}

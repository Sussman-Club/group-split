namespace GroupSplit.Data.Entities;

/// <summary>
/// One account has opened one invitation's link.
/// </summary>
/// <remarks>
/// The invitee's own list, rebuilt from the one thing there is to build it from. An
/// invitation used to be an email address, so the app could hand somebody every group
/// waiting on them by matching their profile; a named person and a link have no address to
/// match, and dropping the address dropped the list with it. What was left was a hole: open
/// the link, sign in, wander off without answering, and the app -- which had just met you --
/// could not offer you the invitation again. The only way back was the chat thread the link
/// arrived in.
/// <para>
/// So opening one is remembered. Not accepting it, not agreeing to anything: only that this
/// account has seen this invitation, which is enough to put it back in front of them.
/// </para>
/// <para>
/// Strictly better than the address matching it replaces, which is worth saying because
/// this looks like a lesser version of it. That one found an invitation only when the group
/// happened to have the same address the person later signed up with; this finds it for
/// whoever actually opened the link, whatever their address is.
/// </para>
/// <para>
/// A join row with a payload, like <see cref="GroupMembership"/>, rather than a collection
/// on either side: nothing reads "everybody who has opened this invitation", and the one
/// read there is -- what this account has open -- goes through the invitation to its group,
/// which no navigation off <see cref="User"/> would shorten.
/// </para>
/// </remarks>
public class InvitationOpened
{
    public Guid InvitationId { get; init; }

    public Guid UserId { get; init; }

    /// <summary>
    /// When they first opened it. Not updated on later visits: what the list wants is the
    /// order the invitations arrived in, and re-reading one does not make it newer.
    /// </summary>
    public required DateTimeOffset OpenedAt { get; init; }
}

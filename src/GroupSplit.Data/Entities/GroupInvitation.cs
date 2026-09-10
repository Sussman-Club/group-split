namespace GroupSplit.Data.Entities;

/// <summary>
/// Somebody the group has named and asked to join, and who has not yet answered.
/// </summary>
/// <remarks>
/// A table of its own rather than a status on <see cref="GroupMembership"/>, which is where
/// the plan first put it. A membership is keyed on (group, user), so an invitation modelled
/// there would need a null user in half a primary key -- and the whole reason invitations
/// exist is the person who has no account behind them yet.
/// <para>
/// It is a pending thing only. Answering it -- claimed, turned down, or withdrawn by the
/// group -- removes the row, and joining is the membership that replaces it. Nothing reads a
/// history of invitations, and a kept row with a status on it would have to be excluded from
/// every query that asks who is waiting. It is also what makes the link below single-use:
/// the token dies with the row it is on.
/// </para>
/// <para>
/// It used to be an email address. That was the handle, the display name and the way the
/// invitee found their own invitations, all at once -- and it meant a group could only ask
/// somebody whose address they had, which is not how most people know the friend they went
/// on the trip with. An invitation is now a name the group chose and a link they can send
/// through whatever they actually talk on.
/// </para>
/// </remarks>
public class GroupInvitation : Entity
{
    public virtual Group Group { get; set; } = null!;

    public Guid GroupId { get; set; }

    /// <summary>
    /// What the group calls them. The only thing it knows, and what every listing shows:
    /// there is no profile to read a name out of until somebody claims this.
    /// </summary>
    /// <remarks>
    /// Not unique within the group. Two people can be called Dani, and refusing the second
    /// would be the app telling a group it has misremembered its own friends.
    /// </remarks>
    public required string Name { get; set; }

    /// <summary>
    /// The random part of the URL the group sends: whoever opens it and claims it becomes
    /// this person.
    /// </summary>
    /// <remarks>
    /// A bearer credential, and a heavier one than <see cref="GroupJoinLink.Token"/>.
    /// That one lets somebody in as themselves, claiming nothing; this one hands over a
    /// position in the group's ledger, so forwarding it gives away somebody else's debts.
    /// Hence single use: claiming removes the row, and with it the token.
    /// </remarks>
    public required string Token { get; set; }

    /// <summary>
    /// The person this invitation is money-wise: the row every share recorded against it
    /// belongs to, from the moment the group makes it.
    /// </summary>
    /// <remarks>
    /// A <see cref="User"/> and not a shape of its own, because a share has to name
    /// somebody and every balance in the app is a sum over <see cref="TransactionSplit"/>
    /// rows keyed on a user. Making a pending invitee a second kind of participant would
    /// mean a nullable user beside a nullable invitation on the hottest table in the model,
    /// and every balance query having to add the two up.
    /// <para>
    /// It is a stand-in: a row with the given name on it, no address, and no
    /// <see cref="User.Identity"/>, so nobody can sign in as it. Claiming the invitation
    /// moves everything it holds onto the claimer's own account and then deletes it, which
    /// is the one moment a position changes hands without any amount changing.
    /// </para>
    /// <para>
    /// Restricted rather than cascading: the stand-in carries the group's money, so losing
    /// the row would take shares with it.
    /// </para>
    /// </remarks>
    public virtual User Participant { get; set; } = null!;

    public Guid ParticipantUserId { get; set; }

    /// <summary>
    /// Who asked. Nullable so that an account deleting itself does not take the
    /// invitations it sent with it -- the group still means them.
    /// </summary>
    public virtual User? InvitedBy { get; set; }

    public Guid? InvitedByUserId { get; set; }

    public required DateTimeOffset InvitedAt { get; set; }
}

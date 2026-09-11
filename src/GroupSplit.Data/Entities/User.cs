namespace GroupSplit.Data.Entities;

/// <summary>
/// Somebody money is recorded against -- whether or not they have ever signed in.
/// </summary>
/// <remarks>
/// That last part is the whole of what is unusual here. A group can name somebody it splits
/// with before they have an account, and start owing them money immediately, so a user row
/// exists from the moment the group types a name: <see cref="Identity"/> is null for them,
/// and <see cref="Email"/> usually is too. They are a real holder of real shares, and
/// claiming an invitation attaches an identity to the row that was already theirs rather
/// than making a second one -- which is what keeps the history from splitting in two on the
/// day they join.
/// <para>
/// So nothing here may assume an identity, and a name is all some rows will ever have. See
/// <c>IGroupParticipants.StandInFor</c> for where they come from and
/// <c>docs/pending-invitees.md</c> for what they may and may not do.
/// </para>
/// </remarks>
public class User : Entity
{
    /// <summary>
    /// The whole of the name for somebody a group typed in: splitting "Ana Ruiz" into two
    /// columns would be the app deciding which half is the surname.
    /// </summary>
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    /// <summary>
    /// Null for anybody who has not signed in. Unique where it is set, so two accounts
    /// cannot answer to one address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// The sign-in behind this person, or null for a stand-in the group named and nobody
    /// has claimed.
    /// </summary>
    public virtual UserIdentity Identity { get; set; } = null!;

    /// <summary>The groups they are a member of. Invitations they have not answered are not here.</summary>
    public virtual ICollection<Group> Groups { get; } = [];

    /// <summary>What they paid for. What they <em>owe</em> is <see cref="TransactionSplit"/>.</summary>
    public virtual ICollection<Transaction> Transactions { get; } = [];
}

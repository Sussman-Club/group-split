namespace GroupSplit.Data.Entities;

/// <summary>
/// Somebody money is recorded against -- whether or not they have ever signed in.
/// </summary>
/// <remarks>
/// That last part is the whole of what is unusual here. A group can name somebody it splits
/// with before they have an account, and start owing them money immediately, so a user row
/// exists from the moment the group types a name: <see cref="Identity"/> is null for them,
/// and <see cref="Email"/> is too. They are a real holder of real shares, and claiming the
/// invitation moves those shares -- and anything they were down as having paid -- onto the
/// claimer's own account, then deletes the stand-in: it never becomes an account itself
/// (see <see cref="UserKind.StandIn"/>), and the history stays whole because every row it
/// held now names the person who claimed it.
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
    /// Which of the three things this row is: somebody with an account, a stand-in a group
    /// named, or an account that has been deleted and anonymised.
    /// </summary>
    /// <remarks>
    /// Stated rather than worked out. The three used to be told apart by what was missing --
    /// no identity and a name is a stand-in, no identity and no name a deleted account -- which
    /// every reader had to know and nothing checked. The table now refuses the combinations
    /// that cannot happen: a stand-in with an address, a deleted account with a name.
    /// </remarks>
    public UserKind Kind { get; set; }

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

/// <summary>What a <see cref="User"/> row stands for.</summary>
public enum UserKind
{
    /// <summary>Somebody who signs in. The default, because it is what provisioning makes.</summary>
    Account,

    /// <summary>
    /// A person a group named before they had an account. Never a member: claiming the
    /// invitation hands the position to the claimer's own account.
    /// </summary>
    StandIn,

    /// <summary>An account its owner deleted. Kept, anonymised, because history names it.</summary>
    Deleted
}

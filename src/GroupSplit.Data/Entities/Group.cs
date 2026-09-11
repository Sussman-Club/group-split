namespace GroupSplit.Data.Entities;

/// <summary>
/// People who share costs, and everything they share them by.
/// </summary>
/// <remarks>
/// The unit every balance is computed within. Nothing crosses a group: a rule, a category
/// and a transaction all belong to one, and "what do I owe overall" is a sum across groups
/// rather than anything a group knows about.
/// <para>
/// There is no group for a person's own spending. A personal transaction has no group at
/// all, which is what personal means -- the hidden "Personal" group that used to stand in
/// for it is gone, along with the places that had to remember to hide it.
/// </para>
/// </remarks>
public class Group : Entity
{
    /// <summary>
    /// Who is in it now. Somebody invited and still to answer is not here yet -- they are
    /// in <see cref="Invitations"/>, and are a participant in the group's money before they
    /// are a member of it.
    /// </summary>
    public virtual ICollection<User> Users { get; } = [];

    /// <summary>The divisions this group keeps, for its categories to point at.</summary>
    public virtual ICollection<SplitRule> SplitRules { get; } = [];

    /// <summary>What it files its spending under.</summary>
    public virtual ICollection<Category> Categories { get; } = [];

    /// <summary>Who has been asked to join and has not answered yet.</summary>
    public virtual ICollection<GroupInvitation> Invitations { get; } = [];

    /// <summary>
    /// The shareable links into this group, live and dead alike -- a revoked one is kept so
    /// that opening it can say it was withdrawn.
    /// </summary>
    public virtual ICollection<GroupJoinLink> JoinLinks { get; } = [];

    public required string Name { get; set; }

    /// <summary>
    /// ISO 4217, and the currency every balance in this group is stated in.
    /// </summary>
    /// <remarks>
    /// Not <c>required</c>, and defaulted here rather than only in the database, because
    /// a group has to have one and there is no sensible moment to ask: every existing
    /// group is in dollars, and a group created without a stated currency means dollars
    /// too. Conversion is out of scope, so a transaction in another currency is refused
    /// rather than converted -- balances that mix currencies are wrong, not merely
    /// unavailable.
    /// </remarks>
    public string Currency { get; init; } = Currencies.Default;
}

/// <summary>
/// The currency facts the model needs, in one place so that the entity, the mapping and the
/// migration cannot disagree about them.
/// </summary>
public static class Currencies
{
    /// <summary>What a group is in when nobody said otherwise.</summary>
    public const string Default = "USD";

    /// <summary>ISO 4217 codes are three characters, which is why the column is fixed-length.</summary>
    public const int CodeLength = 3;
}
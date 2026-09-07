namespace GroupSplit.Data.Entities;

public class Group : Entity
{
    public virtual ICollection<User> Users { get; } = [];

    public virtual ICollection<SplitRule> SplitRules { get; } = [];

    public virtual ICollection<Category> Categories { get; } = [];

    /// <summary>Who has been asked to join and has not answered yet.</summary>
    public virtual ICollection<GroupInvitation> Invitations { get; } = [];

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
    public string Currency { get; set; } = Currencies.Default;
}

public static class Currencies
{
    public const string Default = "USD";

    public const int CodeLength = 3;
}
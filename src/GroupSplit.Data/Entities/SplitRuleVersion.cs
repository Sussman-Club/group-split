namespace GroupSplit.Data.Entities;

/// <summary>
/// What a <see cref="SplitRule"/> said between two moments: how an amount is divided, and
/// from when until when that was the answer.
/// </summary>
/// <remarks>
/// A version is never edited. Changing a rule ends the version that was current -- stamping
/// <see cref="SupersededAt"/> -- and starts a new one, so the row a transaction points at
/// says the same thing tomorrow as it said the day the expense was written. That is what
/// makes "divide this again by the rule it had at the time" a query rather than a guess.
/// <para>
/// The stored <see cref="Transaction.Splits"/> remain the record of what each person
/// actually owed; they are the answer, and this is the question that produced it. Both are
/// needed and neither replaces the other: the amounts survive an edit to the rule, and the
/// version survives an edit to the amount.
/// </para>
/// <para>
/// Data only. What a version <em>does</em> -- divide an amount, say whether it is coherent
/// -- belongs to its handler, resolved by type. Which is also why nothing is declared here
/// about the shape of a split: "a list of participants with weights" is one way to answer,
/// and putting it on the base would quietly rule out every rule that is not proportional.
/// "Omar pays exactly ten and the rest is even" has no weight that expresses it, because
/// weights are normalised by their total and a fixed amount does not scale. A kind like
/// that is a subtype with its own columns and its own handler, and needs nothing from here.
/// </para>
/// </remarks>
public abstract class SplitRuleVersion : Entity
{
    public virtual SplitRule SplitRule { get; set; } = null!;

    /// <summary>
    /// Public, unlike the foreign keys elsewhere that are EF's business, because which rule
    /// a version belongs to is what tells a transaction's version apart from a category's.
    /// </summary>
    public Guid SplitRuleId { get; set; }

    /// <summary>When this became what the rule said.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When it stopped being what the rule said, or null while it still is. At most one
    /// version of a rule is null here, which the database enforces.
    /// </summary>
    public DateTimeOffset? SupersededAt { get; set; }

    /// <summary>
    /// The transactions divided by this version. Why a version is never deleted while
    /// anything points at it: the pointer is the only record of which division a
    /// transaction was written under.
    /// </summary>
    public virtual ICollection<Transaction> Transactions { get; } = [];
}

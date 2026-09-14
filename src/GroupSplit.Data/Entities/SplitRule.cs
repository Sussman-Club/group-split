namespace GroupSplit.Data.Entities;

/// <summary>
/// A division the group keeps and refers to by name -- "Rent", "Even between the flat" --
/// for its categories to point at.
/// </summary>
/// <remarks>
/// Identity only. Which people, with which weights, is a <see cref="SplitRuleVersion"/>,
/// and the rule holds however many of those it has been through: editing a rule ends the
/// current version and starts a new one, so a transaction that named the old one can still
/// be divided by exactly what it was divided by at the time.
/// <para>
/// That is the separation the whole shape is for. A category points here, at the name, and
/// a transaction points at a version, at the division -- so renaming a category, pointing
/// it somewhere else, and editing what a rule says are three independent edits, and none of
/// them restates what anybody owed last March.
/// </para>
/// <para>
/// Nothing here says what shape a split has, for the same reason nothing on
/// <see cref="SplitRuleVersion"/> does: the shape is the version's subtype's business, and
/// naming it here would foreclose every kind that is not a list of weights.
/// </para>
/// </remarks>
public class SplitRule : Entity
{
    public virtual Group Group { get; set; } = null!;

    internal Guid GroupId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Whether the group was given this rule rather than writing it: the one it holds per
    /// member, putting the whole amount on them.
    /// </summary>
    /// <remarks>
    /// Why the rule exists, which is the one thing about it no version says. <em>Who</em> it
    /// is for is not here -- that is the division, and
    /// <c>SplitRuleExtensions.AllFor</c> reads it off the version that states it.
    /// <para>
    /// What the flag buys is the difference between this and the same division a group
    /// writes for itself -- "Ana's gym", on the joint card, every month -- which is an
    /// ordinary rule it may rename, restate and delete. These are not any of the three: they
    /// appear when somebody joins, they stand for one division for as long as they exist, and
    /// an expense may name one without anybody having created it, so a restatement would move
    /// money on expenses recorded under a division nobody chose. <see cref="SplitRule"/> only
    /// records the fact; <c>SplitRuleService</c> is what refuses.
    /// </para>
    /// </remarks>
    public bool BuiltIn { get; init; }

    /// <summary>
    /// Every division this rule has stood for, newest last. Exactly one of them has no
    /// <see cref="SplitRuleVersion.SupersededAt"/>, which is the one it is on now --
    /// <c>SplitRuleExtensions.Current</c> reads it.
    /// </summary>
    public virtual ICollection<SplitRuleVersion> Versions { get; } = [];
}

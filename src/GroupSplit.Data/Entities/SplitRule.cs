namespace GroupSplit.Data.Entities;

/// <summary>
/// How a group divides an amount between its members -- a template, not a record of
/// anything that happened.
/// </summary>
/// <remarks>
/// The rule it replaces was three things at once: the category ("Groceries"), the split,
/// and a chain of versions so that editing the split did not rewrite history. Only the
/// middle one was ever a rule. The label is now <see cref="Category"/>, and history needs
/// no versions because a transaction stores the amounts it was actually divided into --
/// which is a stronger guarantee than versioning gave, since it survives an edit to the
/// rule rather than merely dating it.
/// <para>
/// So this has no dates, and editing it changes what the <em>next</em> expense is
/// pre-filled with and nothing that has already been recorded.
/// </para>
/// <para>
/// Data only. What a rule <em>does</em> -- divide an amount, say whether it is coherent --
/// belongs to its handler, resolved by type, the way rule versions are already handled.
/// Which is also why nothing is declared here about the shape of a split: "a list of
/// participants with weights" is one way to answer, and putting it on the base would
/// quietly rule out every rule that is not proportional. "Omar pays exactly ten and the
/// rest is even" has no weight that expresses it, because weights are normalised by their
/// total and a fixed amount does not scale. A rule like that is a subtype with its own
/// columns and its own handler, and needs nothing from here.
/// </para>
/// </remarks>
public abstract class SplitRule : Entity
{
    public virtual Group Group { get; set; } = null!;

    internal Guid GroupId { get; set; }

    public required string Name { get; set; }
}

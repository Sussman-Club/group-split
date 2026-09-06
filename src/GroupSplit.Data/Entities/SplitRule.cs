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
/// So this has no dates, and editing it changes what the *next* expense is pre-filled
/// with and nothing that has already been recorded.
/// </para>
/// </remarks>
public class SplitRule : Entity
{
    public virtual Group Group { get; set; } = null!;

    internal Guid GroupId { get; set; }

    public required string Name { get; set; }

    public required SplitRuleKind Kind { get; set; }

    public virtual ICollection<SplitRuleParticipant> Participants { get; } = [];
}

/// <summary>
/// What a participant's weight means. The arithmetic does not care -- a weight is a
/// weight -- but the UI has to know whether to show "2 shares" or "40%", and a rule that
/// divides evenly should keep doing so when a member joins rather than freezing today's
/// membership into weights.
/// </summary>
public enum SplitRuleKind
{
    /// <summary>
    /// Equally between the group's current members. Participants are not stored: the
    /// point of an even split is that it follows the membership.
    /// </summary>
    Even = 0,

    /// <summary>
    /// In proportion to whole shares -- "Anabel counts for two".
    /// </summary>
    Shares = 1,

    /// <summary>
    /// In proportion to hundredths of a percent, so 33.33% is 3333 and the weights of a
    /// complete rule sum to 10000.
    /// </summary>
    Percent = 2
}

namespace GroupSplit.Data.Entities;

/// <summary>
/// Not shared: the whole amount is one person's.
/// </summary>
/// <remarks>
/// The first kind in the model that is not proportional, and the reason
/// <see cref="SplitRuleVersion"/> declares nothing about weights: a kind that does not divide
/// in proportion needs to change nothing to exist. Nothing is shared out here -- the amount
/// is not divided, it is assigned -- so there is no weight to hold and no list to hold it in.
/// <para>
/// One person and not a list, which is why this is not a
/// <see cref="WeightedSplitRuleVersion"/> with a single participant on one weight. A weight is
/// a proportion of something being shared out, and a proportion of the whole is not what this
/// says. In a table of its own it also cannot be written with two names in it, with none, or
/// onto a version of a kind that has no use for one.
/// </para>
/// <para>
/// Every group holds one of these per member, provisioned rather than typed -- see
/// <see cref="SplitRule.BuiltIn"/>.
/// </para>
/// </remarks>
public sealed class SoleSplitRuleVersion : SplitRuleVersion
{
    public User User { get; init; } = null!;

    /// <summary>
    /// Who owes the whole amount. Public for the reason
    /// <see cref="SplitRuleParticipant.UserId"/> is: who a rule names is the rule's data,
    /// and every handler reads it.
    /// </summary>
    public Guid UserId { get; set; }
}

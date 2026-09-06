using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// In proportion to stated percentages.
/// </summary>
/// <remarks>
/// Identical to shares once the weights are read, which is the point: the division
/// normalises by the total it is given, so a percentage needs no conversion into anything.
/// The old model converted shares into percentages precisely because the balance query
/// could read nothing else, and that conversion is where the rounding bug lived. What is
/// left that is genuinely this kind's own is what makes it invalid.
/// </remarks>
public class PercentSplitRuleHandler : WeightedSplitRuleHandler<PercentSplitRule>
{
    /// <summary>100%, in hundredths.</summary>
    public const int Total = 10_000;

    protected override IReadOnlyList<SplitWeight> WeightsFor(
        PercentSplitRule rule, IReadOnlyCollection<Guid> members) => StoredWeights(rule);

    public override string? Invalid(PercentSplitRule rule) =>
        base.Invalid(rule)
        ?? (rule.Participants.Count == 0 ? "A percentage rule must name somebody." : null)
        ?? (rule.Participants.Any(participant => participant.Weight < 0)
            ? "A percentage cannot be negative."
            : null)
        // Exactly, not nearly. Whole numbers of hundredths either add up to a hundred
        // percent or they do not, which is why they are whole numbers: the old double
        // percentages needed an epsilon to decide.
        ?? (rule.Participants.Sum(participant => participant.Weight) != Total
            ? "Percentages must add up to 100%."
            : null);
}

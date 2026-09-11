using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// In proportion to stated percentages.
/// </summary>
/// <remarks>
/// Identical to shares once the weights are read, which is the point: the division
/// normalises by the total it is given, so a percentage needs no conversion into anything.
/// The old model converted shares into percentages precisely because the balance query
/// could read nothing else, and that conversion is where the rounding bug lived.
/// <para>
/// The only conversion left is at the edge, between the percent a member types and the
/// hundredths the rule is stored in. Whole hundredths are why validity is exact here where
/// it used to need an epsilon: a <c>double</c> 33.33 is 33.329999999999998, and three of
/// them do not make 100 by any arithmetic a computer does.
/// </para>
/// </remarks>
public class PercentSplitRuleHandler
    : WeightedSplitRuleHandler<PercentSplitRuleVersion>, ISplitRuleFactory<PercentSplitRuleDto>
{
    /// <summary>100%, in hundredths.</summary>
    public const int Total = 10_000;

    private const int PerPercent = 100;

    protected override IReadOnlyList<SplitWeight> WeightsFor(
        PercentSplitRuleVersion ruleVersion, IReadOnlyCollection<Guid> members) => StoredWeights(ruleVersion);

    public override SplitRuleDto ToDto(PercentSplitRuleVersion ruleVersion) =>
        new PercentSplitRuleDto
        {
            Percentages = ruleVersion.Participants.ToDictionary(
                participant => participant.UserId,
                participant => participant.Weight / (decimal)PerPercent)
        };

    public SplitRuleVersion FromDto(PercentSplitRuleDto definition) =>
        Naming(new PercentSplitRuleVersion(),
            definition.Percentages.Select(entry => new KeyValuePair<Guid, int>(
                entry.Key,
                // Rounded once, here, and never again: from this point the rule is whole
                // hundredths and the arithmetic is exact.
                (int)Math.Round(entry.Value * PerPercent, MidpointRounding.AwayFromZero))));

    public override string? Invalid(PercentSplitRuleVersion ruleVersion) =>
        base.Invalid(ruleVersion)
        ?? (ruleVersion.Participants.Count == 0 ? "A percentage rule must name somebody." : null)
        ?? (ruleVersion.Participants.Any(participant => participant.Weight < 0)
            ? "A percentage cannot be negative."
            : null)
        ?? (ruleVersion.Participants.Sum(participant => participant.Weight) != Total
            ? "Percentages must add up to 100%."
            : null);
}

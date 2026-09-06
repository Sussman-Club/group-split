using GroupSplit.Data.Entities;

namespace GroupSplit.Data.Splitting.Handlers;

/// <summary>
/// In proportion to whole shares.
/// </summary>
public class SharesSplitRuleHandler : WeightedSplitRuleHandler<SharesSplitRule>
{
    protected override IReadOnlyList<SplitWeight> WeightsFor(
        SharesSplitRule rule, IReadOnlyCollection<Guid> members) => StoredWeights(rule);

    public override string? Invalid(SharesSplitRule rule) =>
        base.Invalid(rule)
        ?? (rule.Participants.Count == 0 ? "A shares rule must name somebody." : null)
        ?? (rule.Participants.Any(participant => participant.Weight < 0) ? "A share cannot be negative." : null)
        ?? (rule.Participants.All(participant => participant.Weight == 0) ? "Somebody must hold a share." : null);
}

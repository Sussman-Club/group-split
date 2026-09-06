using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// In proportion to whole shares.
/// </summary>
public class SharesSplitRuleHandler
    : WeightedSplitRuleHandler<SharesSplitRule>, ISplitRuleFactory<SharesSplitRuleDto>
{
    protected override IReadOnlyList<SplitWeight> WeightsFor(
        SharesSplitRule rule, IReadOnlyCollection<Guid> members) => StoredWeights(rule);

    public override SplitRuleDto ToDto(SharesSplitRule rule) =>
        new SharesSplitRuleDto
        {
            Shares = rule.Participants.ToDictionary(
                participant => participant.UserId, participant => participant.Weight)
        };

    public SplitRule FromDto(string name, SharesSplitRuleDto definition) =>
        Naming(new SharesSplitRule { Name = name }, definition.Shares);

    public override string? Invalid(SharesSplitRule rule) =>
        base.Invalid(rule)
        ?? (rule.Participants.Count == 0 ? "A shares rule must name somebody." : null)
        ?? (rule.Participants.Any(participant => participant.Weight < 0) ? "A share cannot be negative." : null)
        ?? (rule.Participants.All(participant => participant.Weight == 0) ? "Somebody must hold a share." : null);
}

using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// In proportion to whole shares.
/// </summary>
public class SharesSplitRuleHandler
    : WeightedSplitRuleHandler<SharesSplitRuleVersion>, ISplitRuleFactory<SharesSplitRuleDto>
{
    protected override IReadOnlyList<SplitWeight> WeightsFor(
        SharesSplitRuleVersion ruleVersion, IReadOnlyCollection<Guid> members) => StoredWeights(ruleVersion);

    public override SplitRuleDto ToDto(SharesSplitRuleVersion ruleVersion) =>
        new SharesSplitRuleDto
        {
            Shares = ruleVersion.Participants.ToDictionary(
                participant => participant.UserId, participant => participant.Weight)
        };

    public SplitRuleVersion FromDto(SharesSplitRuleDto definition) =>
        Naming(new SharesSplitRuleVersion(), definition.Shares);

    public override string? Invalid(SharesSplitRuleVersion ruleVersion) =>
        base.Invalid(ruleVersion)
        ?? (ruleVersion.Participants.Count == 0 ? "A shares rule must name somebody." : null)
        ?? (ruleVersion.Participants.Any(participant => participant.Weight < 0) ? "A share cannot be negative." : null)
        ?? (ruleVersion.Participants.All(participant => participant.Weight == 0) ? "Somebody must hold a share." : null);
}

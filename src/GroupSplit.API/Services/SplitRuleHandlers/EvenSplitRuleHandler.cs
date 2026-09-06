using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// Equally between people, one weight apiece.
/// </summary>
/// <remarks>
/// The only kind that reads the membership. A rule naming nobody divides between whoever is
/// in the group now, which keeps it dividing evenly after somebody joins instead of
/// freezing today's members into weights; naming people narrows it instead. Stored weights
/// are ignored either way -- "even" is what makes it even.
/// </remarks>
public class EvenSplitRuleHandler
    : WeightedSplitRuleHandler<EvenSplitRule>, ISplitRuleFactory<EvenSplitRuleDto>
{
    protected override IReadOnlyList<SplitWeight> WeightsFor(
        EvenSplitRule rule, IReadOnlyCollection<Guid> members)
    {
        var among = rule.Participants.Count > 0
            ? rule.Participants.Select(participant => participant.UserId)
            : members;

        return [..among.Select(userId => new SplitWeight(userId, 1))];
    }

    public override SplitRuleDto ToDto(EvenSplitRule rule) =>
        new EvenSplitRuleDto([..rule.Participants.Select(participant => participant.UserId)]);

    public SplitRule FromDto(string name, EvenSplitRuleDto definition) =>
        Naming(new EvenSplitRule { Name = name },
            definition.Among.Select(userId => new KeyValuePair<Guid, int>(userId, 1)));
}

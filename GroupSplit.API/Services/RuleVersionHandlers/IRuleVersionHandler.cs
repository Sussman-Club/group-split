using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public interface IRuleVersionHandler
{
    Task<RuleDetailsResponse> GetRuleDetails(RuleVersion version, CancellationToken ct);
    Task<RuleVersion> CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct);
    bool Equals(RuleVersion existing, RuleVersionDto incoming);
}
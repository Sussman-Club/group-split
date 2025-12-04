using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class PersonalRuleVersionHandler : IRuleVersionHandler
{
    public Task<RuleDetailsResponse> GetRuleDetails(RuleVersion version, CancellationToken ct)
    {
        return Task.FromResult(
            new RuleDetailsResponse
            {
                RuleId = version.Rule.Id,
                RuleVersionId = version.Id,
                Category = version.Rule.Category,
                Version = new PersonalRuleVersionDto()
            });
    }

    public Task<RuleVersion> CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct) =>
        Task.FromResult<RuleVersion>(new PersonalRuleVersion { StartDateTime = DateTime.UtcNow });

    public bool Equals(RuleVersion existing, RuleVersionDto incoming)
        => existing is PersonalRuleVersion && incoming is PersonalRuleVersionDto;
}
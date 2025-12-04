using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class PersonalRuleVersionHandler : IRuleVersionHandler<PersonalRuleVersion, PersonalRuleVersionDto>
{
    public Task<RuleDetailsResponse> GetRuleDetails(PersonalRuleVersion version, CancellationToken ct)
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

    public Task<RuleVersion> CreateEntity(Guid groupId, PersonalRuleVersionDto dto, CancellationToken ct) =>
        Task.FromResult<RuleVersion>(new PersonalRuleVersion { StartDateTime = DateTime.UtcNow });

    public bool Equals(RuleVersion existing, PersonalRuleVersionDto incoming) => existing is PersonalRuleVersion;
}
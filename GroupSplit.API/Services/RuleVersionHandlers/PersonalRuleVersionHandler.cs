using  GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class PersonalRuleVersionHandler : IRuleVersionHandler<PersonalRuleVersion, PersonalRuleVersionDto>
{
    public Task<RuleVersionDto> ToDto(PersonalRuleVersion version, CancellationToken ct)
    {
        return Task.FromResult<RuleVersionDto>(new PersonalRuleVersionDto());
    }

    public Task<RuleVersion> CreateEntity(Guid groupId, PersonalRuleVersionDto dto, CancellationToken ct) =>
        Task.FromResult<RuleVersion>(new PersonalRuleVersion { StartDateTime = DateTime.UtcNow });

    public bool Equals(PersonalRuleVersion existing, PersonalRuleVersionDto incoming) => true;
}
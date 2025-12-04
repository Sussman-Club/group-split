using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public interface IRuleVersionCreateHandler<in TRuleVersionDto> where TRuleVersionDto : RuleVersionDto
{
    Task<RuleVersion> CreateEntity(Guid groupId, TRuleVersionDto dto, CancellationToken ct);
}

public interface IRuleVersionEqualsHandler<in TRuleVersionDto>
    where TRuleVersionDto : RuleVersionDto
{
    bool Equals(RuleVersion existing, TRuleVersionDto incoming);
}

public interface IRuleVersionDetailsHandler<in TRuleVersion> where TRuleVersion : RuleVersion
{
    Task<RuleDetailsResponse> GetRuleDetails(TRuleVersion version, CancellationToken ct);
}

public interface IRuleVersionHandler<in TRuleVersionEntity, in TRuleVersionDto> :
    IRuleVersionDetailsHandler<TRuleVersionEntity>, IRuleVersionCreateHandler<TRuleVersionDto>,
    IRuleVersionEqualsHandler<TRuleVersionDto>
    where TRuleVersionEntity : RuleVersion
    where TRuleVersionDto : RuleVersionDto;
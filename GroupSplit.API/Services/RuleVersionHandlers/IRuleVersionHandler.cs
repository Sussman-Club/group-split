using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public interface IRuleVersionCreateHandler
{
    Task<RuleVersion> CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct);
}

public interface IRuleVersionCreateHandler<in TRuleVersionDto> : IRuleVersionCreateHandler
    where TRuleVersionDto : RuleVersionDto
{
    Task<RuleVersion> IRuleVersionCreateHandler.CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct)
    {
        return dto is not TRuleVersionDto typedDto
            ? throw new InvalidOperationException($"Expected {typeof(TRuleVersionDto).Name}, got {dto.GetType().Name}.")
            : CreateEntity(groupId, typedDto, ct);
    }

    Task<RuleVersion> CreateEntity(Guid groupId, TRuleVersionDto dto, CancellationToken ct);
}

public interface IRuleVersionEqualsHandler
{
    bool Equals(RuleVersion existing, RuleVersionDto incoming);
}

public interface IRuleVersionEqualsHandler<in TRuleVersionDto> : IRuleVersionEqualsHandler
    where TRuleVersionDto : RuleVersionDto
{
    bool Equals(RuleVersion existing, TRuleVersionDto incoming);

    bool IRuleVersionEqualsHandler.Equals(RuleVersion existing, RuleVersionDto incoming)
    {
        return incoming is not TRuleVersionDto typedDto
            ? throw new InvalidOperationException(
                $"Expected {typeof(TRuleVersionDto).Name}, got {incoming.GetType().Name}.")
            : Equals(existing, typedDto);
    }
}

public interface IRuleVersionDetailsHandler
{
    Task<RuleDetailsResponse> GetRuleDetails(RuleVersion version, CancellationToken ct);
}

public interface IRuleVersionDetailsHandler<in TRuleVersion> : IRuleVersionDetailsHandler
    where TRuleVersion : RuleVersion
{
    Task<RuleDetailsResponse> GetRuleDetails(TRuleVersion version, CancellationToken ct);

    Task<RuleDetailsResponse> IRuleVersionDetailsHandler.GetRuleDetails(RuleVersion version, CancellationToken ct)
    {
        return version is not TRuleVersion typedVersion
            ? throw new InvalidOperationException(
                $"Expected {typeof(TRuleVersion).Name}, got {version.GetType().Name}.")
            : GetRuleDetails(typedVersion, ct);
    }
}

public interface IRuleVersionHandler<in TRuleVersionEntity, in TRuleVersionDto> :
    IRuleVersionDetailsHandler<TRuleVersionEntity>, IRuleVersionCreateHandler<TRuleVersionDto>,
    IRuleVersionEqualsHandler<TRuleVersionDto>
    where TRuleVersionEntity : RuleVersion
    where TRuleVersionDto : RuleVersionDto;
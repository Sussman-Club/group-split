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

public interface IRuleVersionEqualsHandler<in TRuleVersion, in TRuleVersionDto> : IRuleVersionEqualsHandler
    where TRuleVersion : RuleVersion
    where TRuleVersionDto : RuleVersionDto
{
    bool Equals(TRuleVersion existing, TRuleVersionDto incoming);

    bool IRuleVersionEqualsHandler.Equals(RuleVersion existing, RuleVersionDto incoming)
    {
        return incoming is not TRuleVersionDto typedDto ||
               existing is not TRuleVersion typedExisting
            ? throw new InvalidOperationException(
                $"Expected {typeof(TRuleVersionDto).Name}, got {incoming.GetType().Name}.")
            : Equals(typedExisting, typedDto);
    }
}

public interface IRuleVersionToDtoHandler
{
    Task<RuleVersionDto> ToDto(RuleVersion version, CancellationToken ct);
}

public interface IRuleVersionToDtoHandler<in TRuleVersion> : IRuleVersionToDtoHandler
    where TRuleVersion : RuleVersion
{
    Task<RuleVersionDto> ToDto(TRuleVersion version, CancellationToken ct);

    async Task<RuleVersionDto> IRuleVersionToDtoHandler.ToDto(RuleVersion version, CancellationToken ct)
    {
        return version is not TRuleVersion typedVersion
            ? throw new InvalidOperationException(
                $"Expected {typeof(TRuleVersion).Name}, got {version.GetType().Name}.")
            : await ToDto(typedVersion, ct);
    }
}

public interface IRuleVersionHandler : IRuleVersionToDtoHandler, IRuleVersionCreateHandler, IRuleVersionEqualsHandler;

public interface IRuleVersionHandler<in TRuleVersionEntity, in TRuleVersionDto> :
    IRuleVersionHandler,
    IRuleVersionToDtoHandler<TRuleVersionEntity>, IRuleVersionCreateHandler<TRuleVersionDto>,
    IRuleVersionEqualsHandler<TRuleVersionEntity, TRuleVersionDto>
    where TRuleVersionEntity : RuleVersion
    where TRuleVersionDto : RuleVersionDto;
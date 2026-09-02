using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class RuleVersionHandler(IServiceProvider provider) : IRuleVersionHandler
{
    public Task<RuleVersionDto> ToDto(RuleVersion version, CancellationToken ct)
    {
        var entityType = version.GetType();
        var handlerType = typeof(IRuleVersionToDtoHandler<>).MakeGenericType(entityType);
        var service = (IRuleVersionToDtoHandler)provider.GetRequiredService(handlerType);
        return service.ToDto(version, ct);
    }

    public Task<RuleVersion> ToEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct)
    {
        var entityType = dto.GetType();
        var handlerType = typeof(IRuleVersionToEntityHandler<>).MakeGenericType(entityType);
        var service = (IRuleVersionToEntityHandler)provider.GetRequiredService(handlerType);
        return service.ToEntity(groupId, dto, ct);
    }

    public async Task<bool> Equals(RuleVersion existing, RuleVersionDto incoming, CancellationToken ct)
    {
        var dtoType = incoming.GetType();
        var entityType = existing.GetType();
        var handlerType = typeof(IRuleVersionEqualsHandler<,>).MakeGenericType(entityType, dtoType);
        var service = provider.GetService(handlerType);
        return service is IRuleVersionEqualsHandler ruleVersionEqualsHandler
               && await ruleVersionEqualsHandler.Equals(existing, incoming, ct);
    }
}
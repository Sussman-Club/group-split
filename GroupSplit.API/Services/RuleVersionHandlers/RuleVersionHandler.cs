using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class RuleVersionHandler(IServiceProvider provider) : IRuleVersionHandler
{
    public Task<RuleDetailsResponse> GetRuleDetails(RuleVersion version, CancellationToken ct)
    {
        var entityType = version.GetType();
        var handlerType = typeof(IRuleVersionDetailsHandler<>).MakeGenericType(entityType);
        var service = (IRuleVersionDetailsHandler)provider.GetRequiredService(handlerType);
        return service.GetRuleDetails(version, ct);
    }

    public Task<RuleVersion> CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct)
    {
        var entityType = dto.GetType();
        var handlerType = typeof(IRuleVersionCreateHandler<>).MakeGenericType(entityType);
        var service = (IRuleVersionCreateHandler)provider.GetRequiredService(handlerType);
        return service.CreateEntity(groupId, dto, ct);
    }

    public bool Equals(RuleVersion existing, RuleVersionDto incoming)
    {
        var dtoType = incoming.GetType();
        var handlerType = typeof(IRuleVersionEqualsHandler<>).MakeGenericType(dtoType);
        var service = (IRuleVersionEqualsHandler)provider.GetRequiredService(handlerType);
        return service.Equals(existing, incoming);
    }
}
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public class RuleVersionHandler(IServiceProvider provider) : IRuleVersionHandler<RuleVersion, RuleVersionDto>
{
    public Task<RuleDetailsResponse> GetRuleDetails(RuleVersion version, CancellationToken ct)
    {
        var entityType = version.GetType();
        var handlerType = typeof(IRuleVersionDetailsHandler<>).MakeGenericType(entityType);
        dynamic service = provider.GetRequiredService(handlerType);
        return service.GetRuleDetails((dynamic)version, ct);
    }

    public Task<RuleVersion> CreateEntity(Guid groupId, RuleVersionDto dto, CancellationToken ct)
    {
        var entityType = dto.GetType();
        var handlerType = typeof(IRuleVersionCreateHandler<>).MakeGenericType(entityType);
        dynamic service = provider.GetRequiredService(handlerType);
        return service.CreateEntity(groupId, (dynamic)dto, ct);
    }

    public bool Equals(RuleVersion existing, RuleVersionDto incoming)
    {
        var dtoType = incoming.GetType();
        var handlerType = typeof(IRuleVersionEqualsHandler<>).MakeGenericType(dtoType);
        dynamic service = provider.GetRequiredService(handlerType);
        return service.Equals(existing, (dynamic)incoming);
    }
}
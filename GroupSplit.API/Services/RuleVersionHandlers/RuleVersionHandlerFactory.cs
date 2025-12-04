using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.RuleVersionHandlers;

public interface IRuleVersionHandlerFactory
{
    IRuleVersionHandler GetHandler(RuleVersionDto dto);
    IRuleVersionHandler GetHandler(RuleVersion ruleVersion);
}

public class RuleVersionHandlerFactory(IServiceProvider provider) : IRuleVersionHandlerFactory
{
    private static readonly Dictionary<Type, Type> HandlerMap = new()
    {
        // DTO → Handler
        { typeof(PercentRuleVersionDto), typeof(PercentRuleVersionHandler) },
        { typeof(PersonalRuleVersionDto), typeof(PersonalRuleVersionHandler) },

        // Entity → Handler
        { typeof(PercentRuleVersion), typeof(PercentRuleVersionHandler) },
        { typeof(PersonalRuleVersion), typeof(PersonalRuleVersionHandler) },
    };

    private IRuleVersionHandler GetHandler(Type type)
    {
        if (!HandlerMap.TryGetValue(type, out var handlerType))
            throw new InvalidOperationException($"No handler registered for type {type.Name}");

        return (IRuleVersionHandler)provider.GetRequiredService(handlerType);
    }

    public IRuleVersionHandler GetHandler(RuleVersionDto dto)
        => GetHandler(dto.GetType());

    public IRuleVersionHandler GetHandler(RuleVersion version)
        => GetHandler(version.GetType());
}
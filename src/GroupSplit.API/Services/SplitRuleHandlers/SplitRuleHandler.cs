using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// Finds the handler for whatever rule -- or whatever definition -- it is given, and asks
/// it.
/// </summary>
/// <remarks>
/// The only place that has to know the kinds exist, and it does not know them by name: it
/// makes the generic interface from the runtime type and asks the container. A kind added
/// later needs no change here.
/// <para>
/// Two lookups because there are two directions. Reading a rule is keyed by the entity;
/// building one is keyed by the definition, since on the way in the entity does not exist
/// yet.
/// </para>
/// <para>
/// The runtime type is the real one because the context does not use lazy-loading proxies;
/// were that turned on, a proxy's type would not be the entity's and the lookup would have
/// to unwrap it first.
/// </para>
/// </remarks>
public class SplitRuleHandler(IServiceProvider provider) : ISplitRuleHandler, ISplitRuleFactory
{
    public IReadOnlyList<SplitAmount> Divide(
        SplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members) =>
        For(rule).Divide(rule, amount, payerId, members);

    public string? Invalid(SplitRule rule) => For(rule).Invalid(rule);

    public SplitRuleDto ToDto(SplitRule rule) => For(rule).ToDto(rule);

    public SplitRule FromDto(string name, SplitRuleDto definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var factoryType = typeof(ISplitRuleFactory<>).MakeGenericType(definition.GetType());

        return ((ISplitRuleFactory)provider.GetRequiredService(factoryType)).FromDto(name, definition);
    }

    private ISplitRuleHandler For(SplitRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var handlerType = typeof(ISplitRuleHandler<>).MakeGenericType(rule.GetType());

        return (ISplitRuleHandler)provider.GetRequiredService(handlerType);
    }
}

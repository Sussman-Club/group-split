using GroupSplit.Data.Entities;
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
        SplitRuleVersion ruleVersion, Transaction transaction, IReadOnlyCollection<Guid> members) =>
        For(ruleVersion).Divide(ruleVersion, transaction, members);

    public string? Invalid(SplitRuleVersion ruleVersion) => For(ruleVersion).Invalid(ruleVersion);

    public SplitRuleDto ToDto(SplitRuleVersion ruleVersion) => For(ruleVersion).ToDto(ruleVersion);

    /// <summary>
    /// Two versions of different kinds are never the same division, which is answered here
    /// rather than by each handler: a handler is only ever asked about its own kind.
    /// </summary>
    public bool SameAs(SplitRuleVersion ruleVersion, SplitRuleVersion other)
    {
        ArgumentNullException.ThrowIfNull(ruleVersion);
        ArgumentNullException.ThrowIfNull(other);

        return ruleVersion.GetType() == other.GetType() && For(ruleVersion).SameAs(ruleVersion, other);
    }

    public SplitRuleVersion FromDto(SplitRuleDto definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var factoryType = typeof(ISplitRuleFactory<>).MakeGenericType(definition.GetType());

        return ((ISplitRuleFactory)provider.GetRequiredService(factoryType)).FromDto(definition);
    }

    private ISplitRuleHandler For(SplitRuleVersion ruleVersion)
    {
        ArgumentNullException.ThrowIfNull(ruleVersion);

        var handlerType = typeof(ISplitRuleHandler<>).MakeGenericType(ruleVersion.GetType());

        return (ISplitRuleHandler)provider.GetRequiredService(handlerType);
    }
}

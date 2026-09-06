using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// Finds the handler for whatever rule it is given, and asks it.
/// </summary>
/// <remarks>
/// The only place that has to know the kinds exist, and it does not know them by name --
/// it makes the generic interface from the rule's own type and asks the container. So a
/// kind added later needs no change here.
/// <para>
/// The runtime type is the real one because the context does not use lazy-loading proxies;
/// were that ever turned on, a proxy's type would not be the entity's and the lookup would
/// have to unwrap it first.
/// </para>
/// </remarks>
public class SplitRuleHandler(IServiceProvider provider) : ISplitRuleHandler
{
    public IReadOnlyList<SplitAmount> Divide(
        SplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members) =>
        HandlerFor(rule).Divide(rule, amount, payerId, members);

    public string? Invalid(SplitRule rule) => HandlerFor(rule).Invalid(rule);

    private ISplitRuleHandler HandlerFor(SplitRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var handlerType = typeof(ISplitRuleHandler<>).MakeGenericType(rule.GetType());

        return (ISplitRuleHandler)provider.GetRequiredService(handlerType);
    }
}

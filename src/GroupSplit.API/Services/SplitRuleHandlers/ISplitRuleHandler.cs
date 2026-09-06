using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// What a split rule does, kept off the rule itself.
/// </summary>
/// <remarks>
/// The entities are data; the behaviour is here, one handler per kind, resolved by the
/// rule's runtime type -- the same shape the rule-version handlers already use. Adding a
/// kind is adding an entity and a handler and registering it. Nothing existing is edited,
/// and there is no switch anywhere to forget a case in.
/// </remarks>
public interface ISplitRuleHandler
{
    /// <summary>
    /// What each person owes on <paramref name="amount"/>.
    /// </summary>
    /// <remarks>
    /// Whatever a handler does, the parts must sum to <paramref name="amount"/>. That is
    /// what every balance in the group rests on, so it is checked where the result is
    /// stored rather than trusted here.
    /// </remarks>
    IReadOnlyList<SplitAmount> Divide(
        SplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members);

    /// <summary>
    /// What is wrong with the rule, or null when nothing is. A complaint rather than an
    /// exception, so the data layer needs none of the API's exception types.
    /// </summary>
    string? Invalid(SplitRule rule);
}

/// <summary>
/// The handler for one kind of rule. The untyped members below are the bridge from the
/// dispatcher, and exist so that an implementation only ever writes the typed ones.
/// </summary>
public interface ISplitRuleHandler<in TRule> : ISplitRuleHandler
    where TRule : SplitRule
{
    IReadOnlyList<SplitAmount> Divide(
        TRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members);

    string? Invalid(TRule rule);

    IReadOnlyList<SplitAmount> ISplitRuleHandler.Divide(
        SplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members) =>
        rule is not TRule typedRule
            ? throw new InvalidOperationException(
                $"Expected {typeof(TRule).Name}, got {rule.GetType().Name}.")
            : Divide(typedRule, amount, payerId, members);

    string? ISplitRuleHandler.Invalid(SplitRule rule) =>
        rule is not TRule typedRule
            ? throw new InvalidOperationException(
                $"Expected {typeof(TRule).Name}, got {rule.GetType().Name}.")
            : Invalid(typedRule);
}

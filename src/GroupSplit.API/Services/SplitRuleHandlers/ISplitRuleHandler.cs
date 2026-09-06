using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// What a split rule does, kept off the rule itself.
/// </summary>
/// <remarks>
/// The entities are data; the behaviour is here, one handler per kind, resolved by type --
/// the same shape the rule-version handlers use. Adding a kind is adding an entity, a DTO
/// and a handler, and registering it. Nothing existing is edited, and there is no switch
/// anywhere to forget a case in.
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
    /// exception, so a handler needs none of the API's exception types.
    /// </summary>
    string? Invalid(SplitRule rule);

    /// <summary>The rule as the client sees it.</summary>
    SplitRuleDto ToDto(SplitRule rule);
}

/// <summary>
/// Builds a rule from what the client sent. Keyed by the DTO rather than the entity,
/// because on the way in the DTO is all there is.
/// </summary>
public interface ISplitRuleFactory
{
    SplitRule FromDto(string name, SplitRuleDto definition);
}

/// <summary>
/// The handler for one kind of rule. The untyped members are the bridge from the
/// dispatcher, and exist so an implementation only ever writes the typed ones.
/// </summary>
public interface ISplitRuleHandler<in TRule> : ISplitRuleHandler
    where TRule : SplitRule
{
    IReadOnlyList<SplitAmount> Divide(
        TRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members);

    string? Invalid(TRule rule);

    SplitRuleDto ToDto(TRule rule);

    IReadOnlyList<SplitAmount> ISplitRuleHandler.Divide(
        SplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members) =>
        Divide(Expected(rule), amount, payerId, members);

    string? ISplitRuleHandler.Invalid(SplitRule rule) => Invalid(Expected(rule));

    SplitRuleDto ISplitRuleHandler.ToDto(SplitRule rule) => ToDto(Expected(rule));

    private static TRule Expected(SplitRule rule) =>
        rule as TRule ?? throw new InvalidOperationException(
            $"Expected {typeof(TRule).Name}, got {rule.GetType().Name}.");
}

/// <inheritdoc cref="ISplitRuleFactory"/>
public interface ISplitRuleFactory<in TDto> : ISplitRuleFactory
    where TDto : SplitRuleDto
{
    SplitRule FromDto(string name, TDto definition);

    SplitRule ISplitRuleFactory.FromDto(string name, SplitRuleDto definition) =>
        definition is not TDto typedDefinition
            ? throw new InvalidOperationException(
                $"Expected {typeof(TDto).Name}, got {definition.GetType().Name}.")
            : FromDto(name, typedDefinition);
}

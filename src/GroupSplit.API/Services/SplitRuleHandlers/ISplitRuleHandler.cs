using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
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
    /// What each person owes on <paramref name="transaction"/>.
    /// </summary>
    /// <remarks>
    /// Whatever a handler does, the parts must sum to the transaction's amount. That is what
    /// every balance in the group rests on, so it is checked where the result is stored
    /// rather than trusted here.
    /// <para>
    /// The transaction rather than an amount and a payer, which is what this took until
    /// itemised rules needed more than those two. They were always the transaction's own --
    /// no caller ever passed anything else -- so nothing is lost, and a caller can no longer
    /// hand over an amount belonging to one expense and a payer belonging to another.
    /// </para>
    /// <para>
    /// What a handler may read off it is whatever it needs and the caller has loaded, which
    /// is a real coupling and not a free one: <see cref="ItemizedSplitRuleHandler"/> reads
    /// <see cref="Expense.Receipt"/>, so every path that might divide has to load it.
    /// <c>ExpenseSplitter.LoadBillIfItDividesByOne</c> is where that is arranged, by id, so
    /// that no caller has to remember an Include.
    /// </para>
    /// </remarks>
    IReadOnlyList<SplitAmount> Divide(
        SplitRuleVersion ruleVersion, Transaction transaction, IReadOnlyCollection<Guid> members);

    /// <summary>
    /// What is wrong with the rule, or null when nothing is. A complaint rather than an
    /// exception, so a handler needs none of the API's exception types.
    /// </summary>
    string? Invalid(SplitRuleVersion ruleVersion);

    /// <summary>The rule as the client sees it.</summary>
    SplitRuleDto ToDto(SplitRuleVersion ruleVersion);

    /// <summary>
    /// Whether two versions say the same thing, and so whether editing a rule into
    /// <paramref name="other"/> is a change at all.
    /// </summary>
    /// <remarks>
    /// Asked of the kind rather than answered by comparing DTOs, because what counts as the
    /// same division is the kind's business: an even rule naming nobody and one naming
    /// everybody divide alike today and would stop agreeing the moment somebody joins, so
    /// they are not the same rule. A kind that got this wrong would leave a rule's history
    /// either littered with versions that changed nothing or missing the one edit that
    /// mattered.
    /// </remarks>
    bool SameAs(SplitRuleVersion ruleVersion, SplitRuleVersion other);
}

/// <summary>
/// Builds a rule from what the client sent. Keyed by the DTO rather than the entity,
/// because on the way in the DTO is all there is.
/// </summary>
public interface ISplitRuleFactory
{
    /// <summary>
    /// A version of whatever kind <paramref name="definition"/> is, belonging to no rule
    /// yet. The name is the rule's and not the version's, so it is not asked for here.
    /// </summary>
    SplitRuleVersion FromDto(SplitRuleDto definition);
}

/// <summary>
/// The handler for one kind of rule. The untyped members are the bridge from the
/// dispatcher, and exist so an implementation only ever writes the typed ones.
/// </summary>
public interface ISplitRuleHandler<in TRule> : ISplitRuleHandler
    where TRule : SplitRuleVersion
{
    IReadOnlyList<SplitAmount> Divide(
        TRule rule, Transaction transaction, IReadOnlyCollection<Guid> members);

    string? Invalid(TRule rule);

    SplitRuleDto ToDto(TRule rule);

    bool SameAs(TRule rule, TRule other);

    IReadOnlyList<SplitAmount> ISplitRuleHandler.Divide(
        SplitRuleVersion ruleVersion, Transaction transaction, IReadOnlyCollection<Guid> members) =>
        Divide(Expected(ruleVersion), transaction, members);

    string? ISplitRuleHandler.Invalid(SplitRuleVersion ruleVersion) => Invalid(Expected(ruleVersion));

    SplitRuleDto ISplitRuleHandler.ToDto(SplitRuleVersion ruleVersion) => ToDto(Expected(ruleVersion));

    bool ISplitRuleHandler.SameAs(SplitRuleVersion ruleVersion, SplitRuleVersion other) =>
        other is TRule typed && SameAs(Expected(ruleVersion), typed);

    private static TRule Expected(SplitRuleVersion ruleVersion) =>
        ruleVersion as TRule ?? throw new InvalidOperationException(
            $"Expected {typeof(TRule).Name}, got {ruleVersion.GetType().Name}.");
}

/// <inheritdoc cref="ISplitRuleFactory"/>
public interface ISplitRuleFactory<in TDto> : ISplitRuleFactory
    where TDto : SplitRuleDto
{
    SplitRuleVersion FromDto(TDto definition);

    SplitRuleVersion ISplitRuleFactory.FromDto(SplitRuleDto definition) =>
        definition is not TDto typedDefinition
            ? throw new InvalidOperationException(
                $"Expected {typeof(TDto).Name}, got {definition.GetType().Name}.")
            : FromDto(typedDefinition);
}

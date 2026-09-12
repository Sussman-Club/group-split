using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// Everything to whoever paid.
/// </summary>
/// <remarks>
/// The shortest handler there is, and the one that shows what the shape costs: no
/// participants, no weights, no arithmetic. It does not go through
/// <see cref="SplitCalculator"/> at all, because there is nothing to divide.
/// </remarks>
public class PayerSplitRuleHandler : ISplitRuleHandler<PayerSplitRuleVersion>, ISplitRuleFactory<PayerSplitRuleDto>
{
    public IReadOnlyList<SplitAmount> Divide(
        PayerSplitRuleVersion ruleVersion, Transaction transaction, IReadOnlyCollection<Guid> members) =>
        [new SplitAmount(transaction.Payer, transaction.Amount)];

    public string? Invalid(PayerSplitRuleVersion ruleVersion) => null;

    public SplitRuleDto ToDto(PayerSplitRuleVersion ruleVersion) => new PayerSplitRuleDto();

    /// <summary>
    /// Always. A payer rule carries nothing, so there is nothing two of them could differ
    /// in -- and an edit that leaves it a payer rule is not an edit.
    /// </summary>
    public bool SameAs(PayerSplitRuleVersion ruleVersion, PayerSplitRuleVersion other) => true;

    public SplitRuleVersion FromDto(PayerSplitRuleDto definition) => new PayerSplitRuleVersion();
}

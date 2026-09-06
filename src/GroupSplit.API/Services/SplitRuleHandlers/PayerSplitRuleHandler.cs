using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
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
public class PayerSplitRuleHandler : ISplitRuleHandler<PayerSplitRule>, ISplitRuleFactory<PayerSplitRuleDto>
{
    public IReadOnlyList<SplitAmount> Divide(
        PayerSplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members) =>
        [new SplitAmount(payerId, amount)];

    public string? Invalid(PayerSplitRule rule) => null;

    public SplitRuleDto ToDto(PayerSplitRule rule) => new PayerSplitRuleDto();

    public SplitRule FromDto(string name, PayerSplitRuleDto definition) =>
        new PayerSplitRule { Name = name };
}

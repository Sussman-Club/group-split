using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Extensions;

public static class ReceiptExtensions
{
    /// <summary>The bill on the wire, in the order it was written down.</summary>
    /// <remarks>
    /// Both flags are the caller's to work out and are passed in rather than read here: what
    /// a bill is worth saying about itself depends on the expense behind it, and this has
    /// only the paper. The count of lines with no rule is the exception -- it is a fact about
    /// the paper -- and it is sent as a count so a screen can say "2 lines need a rule"
    /// without walking the list.
    /// </remarks>
    public static ReceiptResponse ToResponse(this Receipt receipt, ISplitRuleHandler handlers,
        bool canDivide, bool dividesItsExpense) => new(
        receipt.Id, receipt.ExpenseId, receipt.Subtotal, receipt.Tax, receipt.Tip, receipt.Total,
        receipt.Items.Count(i => i.SplitRuleVersionId is null), canDivide, dividesItsExpense,
        receipt.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).Select(i => new ReceiptItemResponse(
            i.Id, i.Name, i.UnitPrice, i.Quantity, i.TotalPrice, i.TaxAmount, i.SplitRuleVersionId,
            i.SplitRuleVersion?.SplitRule.Name,
            i.SplitRuleVersion is { } version ? handlers.ToDto(version) : null)).ToList());
}

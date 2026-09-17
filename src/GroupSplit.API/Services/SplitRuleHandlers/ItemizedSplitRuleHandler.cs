using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// "Divide it by the bill": the rule that has no division of its own and defers to the one
/// each line names.
/// </summary>
/// <remarks>
/// It holds no settings at all, which is why <see cref="Invalid"/> can never fail and two of
/// them are always the same rule -- what it divides by lives on the receipt, not here, so a
/// group needs exactly one of these and it never needs editing.
/// </remarks>
public sealed class ItemizedSplitRuleHandler(ISplitRuleHandler handlers) :
    ISplitRuleHandler<ItemizedSplitRuleVersion>, ISplitRuleFactory<ItemizedSplitRuleDto>
{
    public IReadOnlyList<SplitAmount> Divide(ItemizedSplitRuleVersion rule, SplitRuleContext transaction,
        IReadOnlyCollection<Guid> members)
    {
        // Named rather than quietly divided evenly: an expense filed under this rule with no
        // bill on it is somebody halfway through the job, and an even split is the exact
        // thing they were avoiding by itemising.
        var receipt = transaction.Receipt ?? throw new UnprocessableException(
            ErrorCodes.ReceiptNotFound, "Enter this expense's bill and choose a split rule for each item first.");
        // The bill has to be this expense's money. The two drift apart after the bill is
        // saved -- the amount is edited, or a linked bank row was recorded for a different
        // figure -- so it is checked again here, where the division would otherwise hand out
        // shares of a total nobody paid.
        if (receipt.Total != transaction.Amount)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    "The bill total must equal the expense amount.")
                .WithExtension("total", receipt.Total)
                .WithExtension("amount", transaction.Amount)
                .WithExtension("difference", transaction.Amount - receipt.Total);
        return ReceiptSplitCalculator.Divide(receipt, transaction.Payer, transaction.GroupId, members, handlers);
    }
    public string? Invalid(ItemizedSplitRuleVersion rule) => null;
    public SplitRuleDto ToDto(ItemizedSplitRuleVersion rule) => new ItemizedSplitRuleDto();
    public bool SameAs(ItemizedSplitRuleVersion rule, ItemizedSplitRuleVersion other) => true;
    public SplitRuleVersion FromDto(ItemizedSplitRuleDto definition) => new ItemizedSplitRuleVersion();
}

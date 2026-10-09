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
    /// <para>
    /// A line's rule is shown only when it is a rule of <paramref name="groupId"/>, the group
    /// the expense is in. Anything else is a line still holding another group's rule -- a
    /// bill on an expense moved before a move cleared them -- and that rule's name,
    /// participants and weights are the other group's business, not the business of whoever
    /// can read this one. It reads as a line with no rule, which is also what it is here: a
    /// line may only divide by a rule of its expense's group.
    /// </para>
    /// </remarks>
    public static ReceiptResponse ToResponse(this Receipt receipt, ISplitRuleHandler handlers,
        bool canDivide, bool dividesItsExpense, Guid? groupId)
    {
        SplitRuleVersion? Shown(ReceiptItem item) =>
            item.SplitRuleVersion is { } version && groupId is not null && version.SplitRule.Group.Id == groupId
                ? version
                : null;

        return new ReceiptResponse(
            receipt.Id, receipt.ExpenseId, receipt.Subtotal, receipt.Tax, receipt.Tip, receipt.Total,
            receipt.Items.Count(i => Shown(i) is null), canDivide, dividesItsExpense,
            [
                .. receipt.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).Select(i => new ReceiptItemResponse(
                    i.Id, i.Name, i.UnitPrice, i.Quantity, i.TotalPrice, i.TaxAmount, Shown(i)?.Id,
                    Shown(i)?.SplitRule.Name,
                    Shown(i) is { } version ? handlers.ToDto(version) : null)
                {
                    NormalizedName = i.NormalizedName,
                    Description = i.Description,
                    TaxRate = i.TaxRate,
                    IsTaxable = i.IsTaxable
                })
            ])
        {
            MerchantName = receipt.MerchantName,
            PurchaseDate = receipt.PurchaseDate,
            SourceValues = receipt.SourceValues
        };
    }
}

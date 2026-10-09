using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// What happens to a bill when the expense it divides is edited rather than the bill itself.
/// </summary>
/// <remarks>
/// An expense divided by its bill has two records of what was paid, and each refused to move
/// first: the amount could not change because the bill no longer came to it, and the bill
/// could not change because it had to come to the amount. The edit to the expense is the one
/// that moves now, and the bill follows it -- the same promise every other rule makes, that
/// shares a rule worked out follow the amount they were worked out from.
/// </remarks>
public static class BillRetotal
{
    /// <summary>
    /// Puts the difference between the bill and the new amount on the bill's tip.
    /// </summary>
    /// <remarks>
    /// The tip, because it is the one figure on the bill that belongs to no line and is
    /// spread over all of them by price -- so the difference reaches everybody in proportion
    /// to what they had, and no line's price is made up. It is also what the amount moves
    /// for nearly every time: the tip added on the card after the bill was typed in.
    /// <para>
    /// Lowering the amount by more than the tip is refused rather than taken off a line:
    /// which line was mistyped is something only the person holding the paper knows, and a
    /// tip below nothing is not one any bill has.
    /// </para>
    /// </remarks>
    /// <exception cref="UnprocessableException">
    /// <see cref="ErrorCodes.ReceiptDoesNotAddUp"/>, when the amount is lower than the bill
    /// without its tip.
    /// </exception>
    public static void To(Receipt bill, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(bill);

        var tip = bill.Tip + amount - bill.Total;

        if (tip < 0)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"The bill's tip is {bill.Tip}, so the amount cannot come down by " +
                    $"{bill.Total - amount} without changing what is on the bill. Correct the " +
                    "bill's lines instead, and save the bill with its new total.")
                .WithExtension("total", bill.Total)
                .WithExtension("amount", amount)
                .WithExtension("tip", bill.Tip)
                .WithExtension("difference", amount - bill.Total);

        bill.Tip = tip;
        bill.Total = amount;
    }

    /// <summary>
    /// A detached copy of the bill, for a preview that must divide by a bill the save would
    /// have changed without changing the one the context is tracking.
    /// </summary>
    /// <param name="withoutRules">
    /// True for a bill whose expense is moving to another group, where every line's rule is
    /// the old group's and the save clears them.
    /// </param>
    public static Receipt Copy(Receipt bill, bool withoutRules)
    {
        ArgumentNullException.ThrowIfNull(bill);

        var copy = new Receipt
        {
            Id = bill.Id, ExpenseId = bill.ExpenseId, Subtotal = bill.Subtotal, Tax = bill.Tax,
            Tip = bill.Tip, Total = bill.Total, MerchantName = bill.MerchantName,
            PurchaseDate = bill.PurchaseDate
        };

        foreach (var item in bill.Items)
        {
            copy.Items.Add(new ReceiptItem
            {
                Id = item.Id, ReceiptId = item.ReceiptId, Position = item.Position, Name = item.Name,
                NormalizedName = item.NormalizedName, Description = item.Description,
                UnitPrice = item.UnitPrice, Quantity = item.Quantity, TotalPrice = item.TotalPrice,
                TaxAmount = item.TaxAmount, TaxRate = item.TaxRate, IsTaxable = item.IsTaxable,
                SplitRuleVersionId = withoutRules ? null : item.SplitRuleVersionId,
                SplitRuleVersion = withoutRules ? null : item.SplitRuleVersion
            });
        }

        return copy;
    }

    /// <summary>
    /// Takes every line's rule off a bill whose expense has moved to another group.
    /// </summary>
    /// <remarks>
    /// A line's rule is one of the group's rules, naming the group's people, and the bill is
    /// read by whoever is in the expense's group. Left in place they showed the new group the
    /// old one's rule names, member ids and weights -- and could never divide anything there,
    /// since a line may only name a rule of its expense's own group. Cleared rather than
    /// guessed at: which of the new group's rules a line means is the same question as which
    /// one it meant the first time, and it was a person who answered it then.
    /// </remarks>
    public static void ClearRules(Receipt bill)
    {
        ArgumentNullException.ThrowIfNull(bill);

        foreach (var item in bill.Items)
        {
            item.SplitRuleVersion = null;
            item.SplitRuleVersionId = null;
        }
    }
}

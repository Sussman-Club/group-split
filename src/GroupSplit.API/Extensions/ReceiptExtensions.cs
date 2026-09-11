using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;

namespace GroupSplit.API.Extensions;

/// <summary>
/// A bill, shaped for the wire.
/// </summary>
/// <remarks>
/// In memory rather than as a projection, unlike most of the listings here, because a
/// receipt is read one at a time and its shares have to be worked out by
/// <see cref="ReceiptSplitCalculator"/> -- which is arithmetic no database could be asked to
/// do and no caller should be asked to repeat.
/// </remarks>
public static class ReceiptExtensions
{
    extension(Receipt receipt)
    {
        /// <param name="participants">
        /// Everybody the expense may be divided between, or empty when there is no expense to
        /// divide. What <c>CanDivide</c> checks the claims against: a bill claimed by somebody
        /// who has since left the group is refused by the splitter, and saying yes here is the
        /// last of the four ways this answer used to light up a button that then refused.
        /// </param>
        /// <param name="readerMayDivide">
        /// Whether the person reading may write to this expense at all. Separate from every
        /// other check here, all of which are about the bill: reading reaches further than
        /// writing, so the departed payer of a group expense still sees a bill they cannot
        /// divide.
        /// </param>
        public ReceiptResponse ToResponse(
            IReadOnlyCollection<Guid> participants, bool readerMayDivide)
        {
            var items = receipt.Items
                .Select(item => new ReceiptItemResponse(
                    item.Id,
                    item.Name,
                    item.UnitPrice,
                    item.Quantity,
                    item.TotalPrice,
                    (ReceiptItemSplit)item.Division,
                    [.. ShareOf(item)]))
                .ToList();

            return new ReceiptResponse(
                receipt.Id,
                receipt.ExpenseId,
                receipt.BankTransactionId,
                receipt.Subtotal,
                receipt.Tax,
                receipt.Tip,
                receipt.Total,
                receipt.Items.Count(item =>
                    item.Division == ReceiptItemDivision.Claimed && item.Claims.Count == 0),
                // Three things have to hold, and none of them is about the lines. There has
                // to be an expense to write the shares to; the bill has to still be that
                // expense's money, which a later edit to the amount or a link to a row of a
                // different figure can undo; and the expense has to be shared with somebody,
                // since a bill on a personal one divides between nobody.
                //
                // Said here so a client does not have to know these separately from the rules
                // about the figures -- and said at all because each of them was once a yes
                // here and a refusal one call later, on a button this answer had lit up.
                readerMayDivide
                && receipt.Expense is { } expense
                && expense.GroupId is not null
                && receipt.Total == expense.Amount
                && Claims(receipt).All(participants.Contains)
                && ReceiptSplitCalculator.CanDivide(receipt),
                items);
        }
    }

    /// <summary>
    /// Everybody named anywhere on the bill.
    /// </summary>
    /// <remarks>
    /// Read off the lines rather than off the stored splits, because the two answer different
    /// questions: the splits are who owed something last time it was divided, and this is who
    /// would be given a share if it were divided now.
    /// </remarks>
    private static IEnumerable<Guid> Claims(Receipt receipt) =>
        receipt.Items.SelectMany(item => item.Claims).Select(claim => claim.UserId).Distinct();

    /// <summary>
    /// What each claimant's part of one line comes to, before tax and tip: the line's price
    /// times their weight over the weights on it.
    /// </summary>
    /// <remarks>
    /// Rounded to the cent for display only. The division itself works from the unrounded
    /// figure -- see <see cref="ReceiptSplitCalculator.ClaimedSubtotals"/> -- so these need
    /// not add up to the line's price when three people share an odd amount, and what
    /// anybody actually owes is their split on the expense.
    /// </remarks>
    private static IEnumerable<ReceiptClaimResponse> ShareOf(ReceiptItem item)
    {
        // A line that divides some other way names nobody, so there is nothing to list --
        // and listing the stale claims of a line somebody has since set to Evenly would show
        // people a division that is no longer being applied.
        if (item.Division != ReceiptItemDivision.Claimed)
            return [];

        var totalWeight = item.Claims.Sum(claim => (long)claim.Weight);

        return item.Claims.Select(claim => new ReceiptClaimResponse(
            claim.UserId,
            claim.Weight,
            totalWeight == 0
                ? 0m
                : decimal.Round(item.TotalPrice * claim.Weight / totalWeight, 2)));
    }
}

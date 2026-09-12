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
        /// <param name="canDivide">
        /// Whether asking to divide this part would actually succeed. Decided by the service
        /// rather than here, because it turns on facts a receipt does not carry -- whether
        /// the expense is shared with anybody, whether its amount is still this part of the
        /// bill, whether every claimant is still a participant, and whether the person
        /// reading may write to it at all. Each of those was once a yes on this flag and a
        /// refusal one call later, on a button this answer had lit up.
        /// </param>
        public ReceiptResponse ToResponse(Guid? expenseId, bool canDivide)
        {
            // In the order they are on the paper. Every caller that numbers the lines -- the
            // CLI's `receipts show`, and the split screen's shift-click run -- is reading
            // this order, and a listing that reordered itself between two reads would put
            // somebody's lines in the wrong part without refusing anything.
            var items = receipt.Items
                .OrderBy(item => item.Position)
                .ThenBy(item => item.Id)
                .Select(item => new ReceiptItemResponse(
                    item.Id,
                    item.Name,
                    item.UnitPrice,
                    item.Quantity,
                    item.TotalPrice,
                    item.IsTaxable,
                    item.ExpenseId,
                    (ReceiptItemSplit)item.Division,
                    [.. ShareOf(item)]))
                .ToList();

            return new ReceiptResponse(
                receipt.Id,
                expenseId,
                receipt.BankTransactionId,
                receipt.Subtotal,
                receipt.Tax,
                receipt.Tip,
                receipt.Total,
                receipt.Items.Count(item =>
                    (expenseId is null || item.ExpenseId == expenseId)
                    && item.Division == ReceiptItemDivision.Claimed && item.Claims.Count == 0),
                canDivide,
                items);
        }
    }

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

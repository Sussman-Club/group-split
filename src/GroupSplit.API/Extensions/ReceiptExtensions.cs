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
        public ReceiptResponse ToResponse(
            Guid? expenseId, bool canDivide, bool dividesItsExpense = false)
        {
            // This part of the paper, where a part was asked for.
            //
            // A split charge can put its parts in different groups -- one purchase the
            // flat's, the other your own -- and this projected every line of the bill to
            // whoever could see any one of them: what was bought, for how much, and who was
            // on it by name, which falls back to an email address. The caller is only ever
            // proved entitled to the part they asked about. UnclaimedItemCount below was
            // already counted this way, which is what made the rest an omission rather than
            // a decision.
            //
            // What the other parts came to is still reported, just not what they were: a
            // bill totalling more than the expense is the first thing somebody queries, and
            // "four other lines, 104.53" answers it without naming anybody.
            var mine = expenseId is { } part
                ? receipt.Items.Where(item => item.ExpenseId == part).ToList()
                : receipt.Items.ToList();

            var elsewhere = receipt.Items.Except(mine).ToList();

            // In the order they are on the paper. Every caller that numbers the lines -- the
            // CLI's `receipts show`, and the split screen's shift-click run -- is reading
            // this order, and a listing that reordered itself between two reads would put
            // somebody's lines in the wrong part without refusing anything.
            var items = mine
                .OrderBy(item => item.Position)
                .ThenBy(item => item.Id)
                .Select(item => new ReceiptItemResponse(
                    item.Id,
                    item.Name,
                    item.UnitPrice,
                    item.Quantity,
                    item.TotalPrice,
                    item.TaxAmount,
                    item.ExpenseId,
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
                mine.Count(item => item.Claims.Count == 0),
                canDivide,
                dividesItsExpense,
                elsewhere.Count,
                elsewhere.Sum(item => item.TotalPrice),
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
        var totalWeight = item.Claims.Sum(claim => (long)claim.Weight);

        return item.Claims.Select(claim => new ReceiptClaimResponse(
            claim.UserId,
            People.Display(claim.User),
            claim.Weight,
            totalWeight == 0
                ? 0m
                : decimal.Round(item.TotalPrice * claim.Weight / totalWeight, 2)));
    }
}

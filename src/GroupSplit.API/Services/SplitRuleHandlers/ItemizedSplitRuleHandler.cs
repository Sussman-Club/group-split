using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services.SplitRuleHandlers;

/// <summary>
/// By the bill attached to the expense.
/// </summary>
/// <remarks>
/// The first handler that reads something off the transaction other than its amount and its
/// payer, and the reason <see cref="ISplitRuleHandler.Divide"/> takes the transaction at all.
/// Everything it needs is on <see cref="Expense.Receipt"/>; the version itself carries
/// nothing.
/// <para>
/// The arithmetic is <see cref="ReceiptSplitCalculator"/>'s and not this class's, so an
/// itemised division rounds exactly the way every other division does -- truncated to the
/// cent, remainder to the payer, parts summing to the whole. This is the seam between a rule
/// and a bill, and nothing more.
/// </para>
/// </remarks>
public class ItemizedSplitRuleHandler
    : ISplitRuleHandler<ItemizedSplitRuleVersion>, ISplitRuleFactory<ItemizedSplitRuleDto>
{
    /// <summary>
    /// What the bill says each person owed.
    /// </summary>
    /// <remarks>
    /// <paramref name="members"/> is read for one thing only: the lines divided
    /// <see cref="ReceiptItemDivision.Evenly"/>, which name nobody and so have to be told who
    /// everybody is. It is deliberately <em>not</em> used to filter claims, which is where
    /// this parts company with every proportional rule. Those drop anybody who has left the
    /// group and redistribute their weight, because a rule names people in advance and the
    /// group moves on without them. A bill names who <em>ate</em>, and there is no honest way
    /// to redistribute a steak somebody ordered. A claim by a departed member is caught where
    /// it can be answered -- <c>ExpenseSplitter</c> refuses the resulting shares with
    /// <c>SPLIT_USER_NOT_IN_GROUP</c> -- and the fix is to say who is paying for it, not to
    /// guess.
    /// </remarks>
    public IReadOnlyList<SplitAmount> Divide(
        ItemizedSplitRuleVersion rule, Transaction transaction, IReadOnlyCollection<Guid> members)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // A transfer can carry no bill, and neither can an expense nobody has itemised. Both
        // are refused by name: falling back to an even split would hand five people an equal
        // share of a dinner that was filed under this rule precisely so that it would not be
        // divided that way, and nothing downstream would ever say so.
        var receipt = (transaction as Expense)?.Receipt
                      ?? throw new UnprocessableException(ErrorCodes.ReceiptNotFound,
                              "This expense is split by its bill, and there is no bill on it yet. " +
                              "Attach the receipt and say who had what, or file it under " +
                              "something else.")
                          .WithExtension("transactionId", transaction.Id);

        // The bill has to be the money this expense actually is. It stops being so the
        // moment somebody edits the amount -- the bank settled higher, a tip was added -- and
        // this is the only place that can say which of the two moved.
        //
        // Caught here rather than left to ExpenseSplitter's sum check, which is right that
        // something is wrong and blames the shares: "the shares add up to 120.00, but the
        // expense is 125.00" describes amounts nobody typed, on an edit to a field that is
        // not the shares, with no hint that a receipt is involved at all.
        if (receipt.Total != transaction.Amount)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"This expense is split by its bill, and the bill comes to {receipt.Total} " +
                    $"while the expense is {transaction.Amount}. Update the bill to match, or " +
                    "remove it and divide the expense some other way.")
                .WithExtension("transactionId", transaction.Id)
                .WithExtension("receiptTotal", receipt.Total)
                .WithExtension("amount", transaction.Amount);

        return ReceiptSplitCalculator.Divide(receipt, transaction.Payer, members);
    }

    /// <summary>
    /// Nothing, always. An itemised rule carries no division to be wrong: what makes one
    /// unusable is the state of a bill, which is a fact about an expense and not about the
    /// rule, and is reported when the expense is divided.
    /// </summary>
    public string? Invalid(ItemizedSplitRuleVersion rule) => null;

    public SplitRuleDto ToDto(ItemizedSplitRuleVersion rule) => new ItemizedSplitRuleDto();

    /// <summary>
    /// Always, like the payer rule and for the same reason: there is nothing two of them
    /// could differ in, so an edit that leaves it itemised is not an edit.
    /// </summary>
    public bool SameAs(ItemizedSplitRuleVersion rule, ItemizedSplitRuleVersion other) => true;

    public SplitRuleVersion FromDto(ItemizedSplitRuleDto definition) => new ItemizedSplitRuleVersion();
}

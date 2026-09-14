using GroupSplit.API.Errors;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// Divides each line by the rule pinned to it and totals the results per person.
/// </summary>
/// <remarks>
/// A line is divided as one amount -- its price, the tax charged on it, and its share of the
/// tip -- rather than the three being divided separately and added up. One division means one
/// rounding, so what each person owes for a line is a figure that came out of their rule
/// once, and the shares still sum to the bill exactly.
/// </remarks>
public static class ReceiptSplitCalculator
{
    public static IReadOnlyList<SplitAmount> Divide(Receipt receipt, Guid payer, Guid? groupId,
        IReadOnlyCollection<Guid> members, ISplitRuleHandler handlers, bool subtotalOnly = false)
    {
        RefuseIfFiguresDisagree(receipt);
        var items = receipt.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
        var missing = items.Where(i => i.SplitRuleVersion is null).ToList();
        if (missing.Count > 0)
            throw new UnprocessableException(ErrorCodes.ReceiptItemsMissingRule,
                "Choose a split rule for every item before dividing the expense.")
                .WithExtension("itemIds", missing.Select(i => i.Id).ToList());

        // The tip, spread over the lines by price before anything is divided: it is the one
        // figure on the bill that belongs to no line, and weighing it by price is the only
        // apportioning that does not turn on who ordered the expensive thing. Truncated to
        // the cent per line, so the leftover has to be placed deliberately below.
        var tips = new Dictionary<Guid, decimal>();
        var placed = 0m;
        foreach (var item in items)
        {
            var weight = receipt.Subtotal == 0 ? 1m / items.Count : item.TotalPrice / receipt.Subtotal;
            tips[item.Id] = decimal.Truncate(receipt.Tip * weight * 100m) / 100m;
            placed += tips[item.Id];
        }
        // The leftover cent goes to the largest line, which is the line whose own rounding
        // moves it least. Stable order breaks equal-price ties, including a bill of free
        // items with a tip -- otherwise the same bill could place it differently depending on
        // how it happened to be loaded.
        var remainderItem = items.OrderByDescending(i => i.TotalPrice).ThenBy(i => i.Position)
            .ThenBy(i => i.Id).First();
        tips[remainderItem.Id] += receipt.Tip - placed;

        var totals = new Dictionary<Guid, decimal>();
        foreach (var item in items)
        {
            var version = item.SplitRuleVersion!;
            ValidateRule(version, groupId, members, handlers);
            // Subtotals only, for the reading that shows what somebody's food came to beside
            // what they owe: the same division of the same lines, with the extras left off.
            var amount = item.TotalPrice + (subtotalOnly ? 0 : item.TaxAmount + tips[item.Id]);
            var shares = handlers.Divide(version, new SplitRuleContext(amount, payer, groupId), members);
            // A handler that lost a cent, or paid somebody who is not in the group, is a
            // defect rather than a bad bill -- but it is caught here all the same, because
            // the alternative is an expense whose shares do not come to what was paid and
            // nothing able to say which line did it.
            if (shares.Sum(s => s.Amount) != amount || shares.Any(s => !members.Contains(s.UserId)))
                throw new UnprocessableException(ErrorCodes.SplitsDoNotSumToAmount,
                    $"The rule for {item.Name} did not produce a valid division.");
            foreach (var share in shares)
                totals[share.UserId] = totals.GetValueOrDefault(share.UserId) + share.Amount;
        }
        return totals.OrderBy(p => p.Key).Select(p => new SplitAmount(p.Key, p.Value)).ToList();
    }

    /// <summary>
    /// Whether a rule may be a line's rule, checked where a line is saved as well as where
    /// one is divided.
    /// </summary>
    /// <remarks>
    /// Both, because the two happen at different moments and the answer can change in
    /// between: a version that was fine when the bill was typed names somebody who has since
    /// left. Refusing on the save alone would leave a bill nobody could divide and nothing
    /// able to say why; refusing only on the division would let a line be saved against
    /// another group's rule and go wrong later.
    /// </remarks>
    public static void ValidateRule(SplitRuleVersion version, Guid? groupId,
        IReadOnlyCollection<Guid> members, ISplitRuleHandler handlers)
    {
        // A bill inside a line of a bill divides nothing: the itemized handler asks the
        // expense for its receipt, and a line has none.
        if (version is ItemizedSplitRuleVersion)
            throw new ValidationException(ErrorCodes.ReceiptInvalid,
                "An item cannot use an itemized split rule. Choose even, percentage, shares, or all for one.");
        // Whose rule it is has to be whose expense it is. A personal expense has no group
        // and so can have no line rules either, which is the null here rather than a
        // separate refusal.
        if (groupId is null || version.SplitRule.Group.Id != groupId)
            throw new ValidationException(ErrorCodes.ReceiptInvalid,
                "An item's split rule must belong to the expense's group.");
        if (handlers.Invalid(version) is { } error)
            throw new ValidationException(ErrorCodes.ReceiptInvalid, error);
        if ((version is WeightedSplitRuleVersion weighted &&
             weighted.Participants.Any(p => !members.Contains(p.UserId))) ||
            (version is SoleSplitRuleVersion sole && !members.Contains(sole.UserId)))
            throw new ConflictException(ErrorCodes.SplitUserNotInGroup,
                "An item's rule names somebody who is no longer in the group. Choose a different version.");
    }

    /// <summary>
    /// Whether the bill describes itself: the lines come to the subtotal, the per-line tax
    /// comes to the bill's tax, and the three come to the total.
    /// </summary>
    /// <remarks>
    /// Separate from the division and run before it, because a bill that contradicts itself
    /// has to be named as a bad bill. Reported after dividing it would be a refusal about
    /// the shares, which cannot say which half of the paper was mistyped.
    /// </remarks>
    public static void RefuseIfFiguresDisagree(Receipt receipt)
    {
        if (receipt.Items.Count == 0)
            throw new ValidationException(ErrorCodes.ReceiptInvalid, "A bill needs at least one item.");
        if (new[] { receipt.Subtotal, receipt.Tax, receipt.Tip, receipt.Total }.Any(InvalidMoney) ||
            receipt.Items.Any(i => string.IsNullOrWhiteSpace(i.Name) || i.Name.Length > 128 ||
                InvalidMoney(i.UnitPrice) || InvalidMoney(i.TotalPrice) || InvalidMoney(i.TaxAmount) ||
                i.Quantity <= 0 || decimal.Round(i.Quantity, 3) != i.Quantity))
            throw new ValidationException(ErrorCodes.ReceiptInvalid,
                "Use nonnegative amounts with at most two decimal places and positive quantities with at most three.");
        if (receipt.Subtotal + receipt.Tax + receipt.Tip != receipt.Total ||
            receipt.Items.Sum(i => i.TotalPrice) != receipt.Subtotal ||
            receipt.Items.Sum(i => i.TaxAmount) != receipt.Tax)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                "The items, their tax, and the tip must add up to the bill total.");
    }

    private static bool InvalidMoney(decimal value) => value < 0 || decimal.Round(value, 2) != value;
}

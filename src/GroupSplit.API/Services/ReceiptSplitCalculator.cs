using GroupSplit.API.Errors;
using GroupSplit.API.Services.SplitRuleHandlers;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
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
    /// <summary>Writes the tax implied by the receipt's rates and taxable flags. Existing
    /// receipts with no tax metadata retain their manually reviewed line tax values.</summary>
    public static void CalculateLineTaxes(Receipt receipt)
    {
        var items = receipt.Items.OrderBy(item => item.Position).ThenBy(item => item.Id).ToList();
        var taxes = CalculateTaxes(receipt, items);
        foreach (var item in items)
            item.TaxAmount = taxes[item.Id];
    }

    public static IReadOnlyList<SplitAmount> Divide(Receipt receipt, Guid payer, Guid? groupId,
        IReadOnlyCollection<Guid> members, ISplitRuleHandler handlers, bool subtotalOnly = false)
    {
        RefuseIfFiguresDisagree(receipt);
        var items = receipt.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
        var taxes = CalculateTaxes(receipt, items);
        var missing = items.Where(i => i.SplitRuleVersion is null).ToList();
        if (missing.Count > 0)
            throw new UnprocessableException(ErrorCodes.ReceiptItemsMissingRule,
                "Choose a split rule for every item before dividing the expense.")
                .WithExtension("itemIds", missing.Select(i => i.Id).ToList());

        // Coupons are negative lines, not food that received a negative share of the tip.
        // Only purchased lines carry the tip; an all-free bill shares it evenly.
        var tipWeights = items.Select(item => Math.Max(0m, item.TotalPrice)).ToArray();
        if (tipWeights.All(weight => weight == 0m))
            tipWeights = items.Select(_ => 1m).ToArray();
        var tipShares = Allocate(receipt.Tip, tipWeights);
        var tips = items.Select((item, index) => (Id: item.Id, Tip: tipShares[index]))
            .ToDictionary(pair => pair.Id, pair => pair.Tip);

        var totals = new Dictionary<Guid, decimal>();
        foreach (var item in items)
        {
            var version = item.SplitRuleVersion!;
            ValidateRule(version, groupId, members, handlers);
            // Subtotals only, for the reading that shows what somebody's items came to beside
            // what they owe: the same division of the same lines, with the extras left off.
            var amount = item.TotalPrice + (subtotalOnly ? 0 : taxes[item.Id] + tips[item.Id]);
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
        // A coupon divided among people who bought less than it takes off would credit them
        // on an expense they did not pay. Refused, so the coupon is put with the lines it
        // actually discounts rather than turned into money owed to somebody.
        if (!subtotalOnly && totals.Where(total => total.Value < 0).Select(total => total.Key).ToList()
                is { Count: > 0 } credited)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    "A discount line leaves somebody owing less than nothing. Split it with the items it discounts.")
                .WithExtension("userIds", credited);
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
        // Whose rule it is has to be whose expense it is. A personal group has no rules, so
        // a personal expense can have no line rules either, without a separate refusal.
        if (version.SplitRule.Group.Id != groupId)
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
                InvalidSignedMoney(i.UnitPrice) || InvalidSignedMoney(i.TotalPrice) || InvalidSignedMoney(i.TaxAmount) ||
                i.TaxRate is < 0 or > 100 || i.TaxRate is { } rate && decimal.Round(rate, 4) != rate ||
                i.IsTaxable == false && i.TaxRate is > 0 ||
                i.Quantity <= 0 || decimal.Round(i.Quantity, 3) != i.Quantity))
            throw new ValidationException(ErrorCodes.ReceiptInvalid,
                "Use cent-precision line amounts and positive quantities with at most three decimal places.");
        var itemTotal = receipt.Items.Sum(i => i.TotalPrice);
        var taxItems = receipt.Items.OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
        var itemTax = CalculateTaxes(receipt, taxItems);
        if (receipt.Subtotal + receipt.Tax + receipt.Tip != receipt.Total)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    "The subtotal, tax, and tip do not add up to the bill total.")
                .WithExtension("subtotal", receipt.Subtotal)
                .WithExtension("tax", receipt.Tax)
                .WithExtension("tip", receipt.Tip)
                .WithExtension("total", receipt.Total);
        if (itemTotal != receipt.Subtotal)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    "The line totals do not add up to the bill subtotal.")
                .WithExtension("itemTotal", itemTotal)
                .WithExtension("subtotal", receipt.Subtotal);
        if (itemTax.Values.Sum() != receipt.Tax)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    "The line tax does not add up to the bill tax.")
                .WithExtension("itemTax", itemTax.Values.Sum())
                .WithExtension("tax", receipt.Tax);

        if (receipt.Items.Any(item => itemTax[item.Id] != item.TaxAmount))
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                "The calculated line tax does not match the receipt tax.");
    }

    /// <summary>Calculates each signed line's tax. A coupon with a printed rate can
    /// contribute negative tax. A line with no rate keeps the tax it was given, and only
    /// what the receipt tax still leaves over is spread across such lines.</summary>
    private static IReadOnlyDictionary<Guid, decimal> CalculateTaxes(Receipt receipt, IReadOnlyList<ReceiptItem> items)
    {
        var taxes = ReceiptTaxes.Calculate(receipt.Tax, items.Select(item =>
            new ReceiptTaxLine(item.TotalPrice, item.TaxAmount, item.TaxRate, item.IsTaxable)).ToArray());
        return items.Select((item, index) => (item.Id, Tax: taxes[index]))
            .ToDictionary(item => item.Id, item => item.Tax);
    }

    private static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0) return [];
        var safeWeights = weights.Select(weight => Math.Max(0m, weight)).ToArray();
        var total = safeWeights.Sum();
        if (total == 0) return new decimal[weights.Count];
        var cents = RoundMoney(Math.Max(0m, amount)) * 100m;
        var exact = safeWeights.Select(weight => cents * weight / total).ToArray();
        var result = exact.Select(decimal.Floor).ToArray();
        foreach (var index in Enumerable.Range(0, weights.Count)
                     .OrderByDescending(index => exact[index] - result[index]).ThenBy(index => index)
                     .Take((int)(cents - result.Sum())))
            result[index]++;
        return result.Select(value => value / 100m).ToArray();
    }

    private static decimal RoundMoney(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static bool InvalidMoney(decimal value) => value < 0 || decimal.Round(value, 2) != value;

    private static bool InvalidSignedMoney(decimal value) => decimal.Round(value, 2) != value;
}

using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// Turns a bill into money owed, at two scales: how much of the charge each purchase on it
/// came to, and then what each person owes within one of those purchases.
/// </summary>
/// <remarks>
/// One warehouse charge can be the flat's groceries and a jacket of your own -- two
/// purchases, two expenses, one piece of paper. So the tax and the tip are apportioned
/// twice: between the parts in proportion to the lines each holds, and then inside a part
/// between the people who claimed those lines. A restaurant bill is the same arithmetic
/// with one part, which is why nothing about dividing a dinner changed when this arrived.
/// <para>
/// Tax and tip are weighed differently, and that is not a detail. Tax is charged on goods,
/// and plenty of bills exempt some of them -- weighing it across every line taxes the
/// bananas and lets the jacket off. A tip is a fact about the bill rather than about the
/// goods, so it spreads over everything. Hence two weightings, each apportioned exactly, and
/// a total that is still exact because a sum of exact parts is exact.
/// </para>
/// <para>
/// Every division here ends in <see cref="Spread"/>, which is the one place a whole is cut
/// into parts: truncate to the cent, hand the leftover to one named holder. Nothing in this
/// file rounds on its own, which is what keeps the parts summing to the charge and the
/// shares summing to their part.
/// </para>
/// </remarks>
public static class ReceiptSplitCalculator
{
    /// <summary>
    /// What each purchase on the bill came to: its own lines, plus its share of the tax and
    /// the tip. Keyed by the expense its lines name.
    /// </summary>
    /// <remarks>
    /// The figure that becomes each part's <see cref="Transaction.Amount"/>, and the reason
    /// splitting a charge does not have to be reconciled afterwards: the parts are cut from
    /// the total rather than added up towards it, so they sum to it by construction.
    /// </remarks>
    public static IReadOnlyDictionary<Guid, decimal> PartAmounts(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return Priced(receipt, receipt.Items
            .Where(item => item.ExpenseId is not null)
            .Select(item => (Key: item.ExpenseId!.Value, Item: item)));
    }

    /// <summary>
    /// What each purchase would come to, for a bill somebody is proposing to split -- before
    /// any of the expenses exist. Answered by position, in the order the parts were given.
    /// </summary>
    /// <remarks>
    /// The same arithmetic as <see cref="PartAmounts"/>, asked the only way it can be asked
    /// before there is anything to key on. Splitting a charge has to know what each part
    /// comes to in order to create an expense for it, and an expense cannot be created
    /// without its amount -- so the two are worked out here first and the lines are pointed
    /// at the expenses afterwards.
    /// </remarks>
    public static IReadOnlyList<decimal> AmountsFor(
        Receipt receipt, IReadOnlyList<IReadOnlyList<ReceiptItem>> parts)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(parts);

        var priced = Priced(receipt, parts.SelectMany(
            (part, index) => part.Select(item => (Key: index, Item: item))));

        return [.. Enumerable.Range(0, parts.Count).Select(i => priced.GetValueOrDefault(i))];
    }

    /// <summary>
    /// Each group of lines, plus its share of the bill's tax and tip.
    /// </summary>
    /// <remarks>
    /// Tax over the lines it was charged on; the tip over all of them. Where nothing on the
    /// bill is taxable the tax cannot have come from the goods, so it spreads like the tip
    /// rather than vanishing.
    /// <para>
    /// Cut from the total rather than added up towards it, which is why the parts of a split
    /// charge sum to what the card was charged without anything having to reconcile them.
    /// </para>
    /// </remarks>
    private static Dictionary<TKey, decimal> Priced<TKey>(
        Receipt receipt, IEnumerable<(TKey Key, ReceiptItem Item)> placed)
        where TKey : notnull
    {
        var held = placed.ToList();

        var lines = Sum(held, pair => pair.Key, pair => pair.Item.TotalPrice);
        var taxable = Sum(held.Where(pair => pair.Item.IsTaxable),
            pair => pair.Key, pair => pair.Item.TotalPrice);

        var biggest = Largest(lines);
        var tax = Spread(Cents(receipt.Tax), taxable.Values.Sum() > 0 ? taxable : lines, biggest);
        var tip = Spread(Cents(receipt.Tip), lines, biggest);

        var amounts = new Dictionary<TKey, decimal>();

        foreach (var (key, subtotal) in lines)
            amounts[key] = subtotal + (tax.GetValueOrDefault(key) + tip.GetValueOrDefault(key)) / 100m;

        return amounts;
    }

    /// <summary>
    /// What each person owes on one part of the bill, summing to that part's amount exactly.
    /// </summary>
    /// <param name="expenseId">Which purchase on the bill to divide.</param>
    /// <param name="payerId">
    /// Who paid. They carry the remainder, the same as in any other division.
    /// </param>
    /// <param name="participants">
    /// Everybody the expense may be divided between, for the lines that name nobody.
    /// </param>
    /// <exception cref="UnprocessableException">
    /// The bill does not add up, or a line in this part belongs to nobody -- neither of which
    /// describes a division that could be stored.
    /// </exception>
    public static IReadOnlyList<SplitAmount> Divide(
        Receipt receipt, Guid expenseId, Guid payerId, IReadOnlyCollection<Guid> participants)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(participants);

        RefuseIfFiguresDisagree(receipt);

        var part = PartOf(receipt, expenseId);

        RefuseIfAnythingIsUnclaimed(part);

        var amount = PartAmounts(receipt).GetValueOrDefault(expenseId);

        // The part's three components, each spread by the weighting that belongs to it. Doing
        // it in one pass over a single weighting would be simpler and would charge somebody
        // who only had exempt groceries for a share of the tax on everybody else's.
        var lineTotal = part.Sum(item => item.TotalPrice);
        var taxTotal = Cents(amount) - Cents(lineTotal) - TipShareOf(receipt, expenseId);

        var claimedAll = Claimed(part, payerId, participants);
        var claimedTaxable = Claimed(part.Where(item => item.IsTaxable), payerId, participants);

        if (claimedAll.Count == 0)
            throw new UnprocessableException(ErrorCodes.ReceiptItemsUnclaimed,
                "Nobody has claimed anything in this part of the bill, so there is nothing to divide.");

        var favour = claimedAll.ContainsKey(payerId) ? payerId : Largest(claimedAll);

        var byLines = Spread(Cents(lineTotal), claimedAll, favour);
        var byTax = Spread(taxTotal, claimedTaxable.Values.Sum() > 0 ? claimedTaxable : claimedAll, favour);
        var byTip = Spread(TipShareOf(receipt, expenseId), claimedAll, favour);

        return
        [
            .. claimedAll.Keys.Select(userId => new SplitAmount(userId,
                (byLines.GetValueOrDefault(userId)
                 + byTax.GetValueOrDefault(userId)
                 + byTip.GetValueOrDefault(userId)) / 100m))
        ];
    }

    /// <summary>The lines of one purchase on the bill.</summary>
    public static IReadOnlyList<ReceiptItem> PartOf(Receipt receipt, Guid expenseId)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return [.. receipt.Items.Where(item => item.ExpenseId == expenseId)];
    }

    /// <summary>
    /// Whether dividing this part would succeed -- the bill adds up, and every line in the
    /// part is spoken for.
    /// </summary>
    /// <remarks>
    /// Answered by running the same refusals rather than by restating them. A second copy of
    /// "what makes a part dividable" is a second copy to drift.
    /// </remarks>
    public static bool CanDivide(Receipt receipt, Guid expenseId)
    {
        try
        {
            RefuseIfFiguresDisagree(receipt);

            var part = PartOf(receipt, expenseId);
            RefuseIfAnythingIsUnclaimed(part);

            return part.Any(item => item.Claims.Count > 0);
        }
        catch (UnprocessableException)
        {
            return false;
        }
        catch (ValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses a bill whose own figures contradict each other.
    /// </summary>
    /// <remarks>
    /// Checked when the bill is stored as well as when a part of it is divided, because a
    /// receipt that does not describe the money it claims to is wrong the moment it is
    /// written down -- waiting means the person who mistyped it is not the person who has to
    /// work out what happened.
    /// </remarks>
    public static void RefuseIfFiguresDisagree(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var parts = receipt.Subtotal + receipt.Tax + receipt.Tip;

        if (parts != receipt.Total)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"The subtotal, tax and tip come to {parts}, but the receipt's total is " +
                    $"{receipt.Total}.")
                .WithExtension("subtotal", receipt.Subtotal)
                .WithExtension("tax", receipt.Tax)
                .WithExtension("tip", receipt.Tip)
                .WithExtension("total", receipt.Total);

        var lines = receipt.Items.Sum(item => item.TotalPrice);

        if (lines != receipt.Subtotal)
            throw new UnprocessableException(ErrorCodes.ReceiptDoesNotAddUp,
                    $"The items come to {lines}, but the receipt's subtotal is {receipt.Subtotal}.")
                .WithExtension("itemTotal", lines)
                .WithExtension("subtotal", receipt.Subtotal);
    }

    /// <summary>
    /// Refuses a part with a line belonging to nobody.
    /// </summary>
    /// <remarks>
    /// Only when it comes to dividing, and only over the lines of the part being divided.
    /// Storing a bill with unclaimed lines is the point of storing one before it is divided,
    /// and a line sitting in somebody else's half of the charge is none of this part's
    /// business.
    /// </remarks>
    private static void RefuseIfAnythingIsUnclaimed(IReadOnlyList<ReceiptItem> part)
    {
        var unclaimed = part.Where(item => item.Claims.Count == 0).ToList();

        // Refused rather than spread over everybody. A forgotten line and one the table
        // really did share look identical from here -- which is exactly what marking a line
        // as everybody's exists to tell apart -- and quietly charging five people for one
        // person's steak is the kind of wrong nobody checks for afterwards.
        if (unclaimed.Count > 0)
            throw new UnprocessableException(ErrorCodes.ReceiptItemsUnclaimed,
                    $"{unclaimed.Count} item(s) in this part of the bill belong to nobody, so it " +
                    "cannot be divided yet.")
                .WithExtension("unclaimedItemIds", unclaimed.ConvertAll(item => item.Id))
                .WithExtension("unclaimedItemNames", unclaimed.ConvertAll(item => item.Name));
    }

    /// <summary>
    /// What each person claimed of these lines, in money, before tax and tip.
    /// </summary>
    /// <remarks>
    /// Unrounded on purpose. These are weights and not amounts -- nobody is charged what this
    /// returns -- so carrying the full precision of a third of a bottle costs nothing and
    /// keeps three equal claims equal, which rounding each to the cent here would not.
    /// </remarks>
    /// <summary>
    /// What each person claimed of these lines, before tax and tip.
    /// </summary>
    /// <remarks>
    /// Public because a preview shows it beside what each person owes: the difference between
    /// the two is their share of the tax and the tip, and seeing both is how somebody checks
    /// that the apportioning did what they expected.
    /// </remarks>
    public static IReadOnlyDictionary<Guid, decimal> ClaimedSubtotals(
        IReadOnlyList<ReceiptItem> lines, Guid payerId, IReadOnlyCollection<Guid> participants)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(participants);

        return Claimed(lines, payerId, participants);
    }

    private static Dictionary<Guid, decimal> Claimed(
        IEnumerable<ReceiptItem> lines, Guid payerId, IReadOnlyCollection<Guid> participants)
    {
        var claimed = new Dictionary<Guid, decimal>();

        void Add(Guid userId, decimal share) =>
            claimed[userId] = claimed.GetValueOrDefault(userId) + share;

        foreach (var item in lines)
        {
            // Nobody had it, as far as the bill says. Skipped rather than spread, and the
            // division as a whole is refused before it gets here -- see
            // RefuseIfAnythingIsUnclaimed.
            if (item.Claims.Count == 0)
                continue;

            if (item.Claims.Any(claim => claim.Weight <= 0))
                throw new ValidationException(ErrorCodes.ReceiptInvalid,
                        $"\"{item.Name}\" has a claim with no share in it. A claim is somebody " +
                        "who had some of the line, so its weight has to be at least one.")
                    .WithExtension("receiptItemId", item.Id);

            var totalWeight = item.Claims.Sum(claim => (long)claim.Weight);

            foreach (var claim in item.Claims)
                Add(claim.UserId, item.TotalPrice * claim.Weight / totalWeight);
        }

        return claimed;
    }

    /// <summary>What of the bill's tip belongs to one part, in cents.</summary>
    private static long TipShareOf(Receipt receipt, Guid expenseId)
    {
        var lines = Sum(receipt.Items.Where(item => item.ExpenseId is not null),
            item => item.ExpenseId!.Value, item => item.TotalPrice);

        return Spread(Cents(receipt.Tip), lines, Largest(lines)).GetValueOrDefault(expenseId);
    }

    /// <summary>
    /// Cuts <paramref name="amount"/> into parts in proportion to <paramref name="weights"/>:
    /// truncated to the cent, with the leftover handed to <paramref name="favour"/>, so the
    /// parts sum to the amount exactly.
    /// </summary>
    /// <remarks>
    /// The one place anything here is rounded, which is what lets every caller above claim
    /// its own result is exact. Truncation rather than rounding, so no holder is ever given
    /// more than their proportion and the leftover is always somebody named rather than
    /// whoever a dictionary happened to enumerate last.
    /// <para>
    /// Weights of zero throughout -- a comped bill, a part of nothing but free items -- means
    /// there is no proportion to divide by, and evenly between whoever is there is what
    /// "in proportion to what you had" degenerates to. It is the only answer that does not
    /// invent an order.
    /// </para>
    /// </remarks>
    private static Dictionary<TKey, long> Spread<TKey>(
        long amount, Dictionary<TKey, decimal> weights, TKey favour)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, long>();

        if (weights.Count == 0 || amount == 0)
        {
            foreach (var key in weights.Keys) result[key] = 0;
            return result;
        }

        var total = weights.Values.Sum();
        var even = total <= 0;
        var placed = 0L;

        foreach (var (key, weight) in weights)
        {
            // Multiplied before divided, and that grouping is the whole of it: dividing
            // first turns a two-to-one share of 30.00 into 0.666... and truncates the
            // product to 19.99, so the remainder lands on somebody as a stray cent and a
            // clean 20/10 reads 20.01/9.99. Kept in decimal throughout for the same reason.
            var share = even
                ? amount / weights.Count
                : (long)(amount * weight / total);

            result[key] = share;
            placed += share;
        }

        if (!result.ContainsKey(favour))
            favour = result.Keys.First();

        result[favour] += amount - placed;

        return result;
    }

    private static Dictionary<TKey, decimal> Sum<T, TKey>(
        IEnumerable<T> source, Func<T, TKey> key, Func<T, decimal> value)
        where TKey : notnull
    {
        var totals = new Dictionary<TKey, decimal>();

        foreach (var item in source)
            totals[key(item)] = totals.GetValueOrDefault(key(item)) + value(item);

        return totals;
    }

    private static TKey Largest<TKey>(Dictionary<TKey, decimal> weights)
        where TKey : notnull
    {
        var best = default(TKey)!;
        decimal seen = -1;

        // Ties broken by the lower key, so the same bill divides the same way on every
        // machine and in every enumeration order.
        foreach (var (key, weight) in weights)
            if (weight > seen ||
                (weight == seen && Comparer<TKey>.Default.Compare(key, best) < 0))
            {
                best = key;
                seen = weight;
            }

        return best;
    }

    private static long Cents(decimal amount) => (long)decimal.Round(amount * 100);
}

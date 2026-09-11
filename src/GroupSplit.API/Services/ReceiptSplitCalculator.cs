using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// Turns a bill into what each person owed: their items, plus their share of the tax and the
/// tip.
/// </summary>
/// <remarks>
/// The tax and the tip are apportioned in proportion to what each person claimed, which is
/// the only division of them that does not depend on who happened to order the expensive
/// thing. Somebody holding a quarter of the food owes a quarter of both.
/// <para>
/// That falls out of the arithmetic rather than being done in a second pass. Each person's
/// claimed subtotal becomes their weight, and the weights divide the <em>total</em> -- so a
/// weight of a quarter draws a quarter of the food, a quarter of the tax and a quarter of the
/// tip in one step. Doing it in two, by splitting the subtotal and then splitting the extras,
/// rounds twice and leaves two remainders to place instead of one.
/// </para>
/// <para>
/// Which is also why this does not do its own division: it works out weights and hands them
/// to <see cref="SplitCalculator"/>, the one place the split arithmetic is written. The
/// truncation, the remainder going to the payer, and the guarantee that the parts sum to the
/// whole are all inherited rather than restated, so a receipt's shares round exactly the way
/// a rule's do.
/// </para>
/// </remarks>
public static class ReceiptSplitCalculator
{
    /// <summary>
    /// Weights are held as whole numbers, so a claimed subtotal has to be scaled to one.
    /// Hundredths of a cent, which is finer than any bill and keeps a line shared three ways
    /// from drifting -- see <see cref="ScaleFor"/> for the bills too large to afford it.
    /// </summary>
    private const int PreferredScale = 10_000;

    /// <summary>
    /// What each person owes on <paramref name="receipt"/>, summing to its total exactly.
    /// </summary>
    /// <param name="payerId">
    /// Who paid. They carry the remainder, the same as in any other division.
    /// </param>
    /// <param name="participants">
    /// Everybody the expense may be divided between, for the lines that name nobody. A line
    /// divided <see cref="ReceiptItemDivision.Evenly"/> is shared between these, so the bill
    /// keeps saying "the rest is shared" when somebody joins instead of freezing today's
    /// membership into claims.
    /// </param>
    /// <exception cref="UnprocessableException">
    /// The bill does not add up, or a line belongs to nobody -- neither of which describes a
    /// division that could be stored.
    /// </exception>
    public static IReadOnlyList<SplitAmount> Divide(
        Receipt receipt, Guid payerId, IReadOnlyCollection<Guid> participants)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(participants);

        RefuseIfFiguresDisagree(receipt);
        RefuseIfAnythingIsUnclaimed(receipt);

        var claimed = ClaimedSubtotals(receipt, payerId, participants);

        if (claimed.Count == 0)
            throw new UnprocessableException(ErrorCodes.ReceiptItemsUnclaimed,
                "Nobody has claimed anything on this receipt, so there is nothing to divide.");

        var scale = ScaleFor(receipt.Subtotal);

        var weights = claimed
            .Select(entry => new SplitWeight(entry.Key, (int)decimal.Round(entry.Value * scale)))
            .ToList();

        // A bill whose lines are all free -- comped food with a tip left on it, which does
        // happen -- gives every claimant a weight of zero, and proportions of nothing are
        // not a division SplitCalculator can carry out. Evenly between whoever was there is
        // what "in proportion to what you had" degenerates to when nobody had anything
        // priced, and it is the only answer that does not invent an order.
        if (weights.TrueForAll(weight => weight.Weight == 0))
            weights = [.. weights.Select(weight => weight with { Weight = 1 })];

        return SplitCalculator.Divide(receipt.Total, payerId, weights);
    }

    /// <summary>
    /// What each person claimed, in money, before tax and tip. A line shared between people
    /// is divided between them by their weights on it.
    /// </summary>
    /// <remarks>
    /// Unrounded on purpose. These are weights and not amounts -- nobody is charged what this
    /// returns -- so carrying the full precision of a third of a bottle costs nothing and
    /// keeps three equal claims equal, which rounding each to the cent here would not.
    /// <para>
    /// Public because a preview shows it beside what each person owes: the difference between
    /// the two is their share of the tax and the tip, and seeing both is how somebody checks
    /// that the apportioning did what they expected.
    /// </para>
    /// </remarks>
    public static Dictionary<Guid, decimal> ClaimedSubtotals(
        Receipt receipt, Guid payerId, IReadOnlyCollection<Guid> participants)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(participants);

        var claimed = new Dictionary<Guid, decimal>();

        void Add(Guid userId, decimal share) =>
            claimed[userId] = claimed.GetValueOrDefault(userId) + share;

        foreach (var item in receipt.Items)
        {
            switch (item.Division)
            {
                // Between everybody, naming none of them. Empty participants would be a
                // division by zero; it cannot happen -- an expense always has at least its
                // payer -- but it costs one comparison to not find out the hard way.
                case ReceiptItemDivision.Evenly when participants.Count > 0:
                    foreach (var participant in participants)
                        Add(participant, item.TotalPrice / participants.Count);

                    break;

                case ReceiptItemDivision.Evenly:
                    Add(payerId, item.TotalPrice);
                    break;

                default:
                    // Unclaimed is not an error here: RefuseIfAnythingIsUnclaimed has the
                    // say, and CanDivide asks it separately.
                    if (item.Claims.Count == 0)
                        continue;

                    // A validation refusal and not an unprocessable one, so that
                    // RECEIPT_INVALID means 400 wherever it appears. The wire already refuses
                    // this with [Range]; what reaches here is a row written past the API, and
                    // a code that answered 400 from one door and 422 from another would be a
                    // code a client cannot branch on.
                    if (item.Claims.Any(claim => claim.Weight <= 0))
                        throw new ValidationException(ErrorCodes.ReceiptInvalid,
                                $"\"{item.Name}\" has a claim with no share in it. A claim is " +
                                "somebody who had some of the line, so its weight has to be " +
                                "at least one.")
                            .WithExtension("receiptItemId", item.Id);

                    var totalWeight = item.Claims.Sum(claim => (long)claim.Weight);

                    foreach (var claim in item.Claims)
                        Add(claim.UserId, item.TotalPrice * claim.Weight / totalWeight);

                    break;
            }
        }

        return claimed;
    }

    /// <summary>
    /// Whether dividing this bill would succeed -- it adds up, and every line is claimed.
    /// </summary>
    /// <remarks>
    /// Asked by the response so a client can light the button without having to know the
    /// rules, and answered by running the same refusals rather than by restating them. A
    /// second copy of "what makes a bill dividable" is a second copy to drift.
    /// </remarks>
    public static bool CanDivide(Receipt receipt)
    {
        try
        {
            RefuseIfFiguresDisagree(receipt);
            RefuseIfAnythingIsUnclaimed(receipt);

            // A bill of nothing but unclaimed lines has already been refused above, so what
            // is left to check is that *something* divides it: at least one line that names
            // somebody, or one that is the table's.
            return receipt.Items.Any(item =>
                item.Division != ReceiptItemDivision.Claimed || item.Claims.Count > 0);
        }
        catch (UnprocessableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses a bill whose own figures contradict each other.
    /// </summary>
    /// <remarks>
    /// Checked when the bill is stored as well as when it is divided, because a receipt that
    /// does not describe the money it claims to is wrong the moment it is written down --
    /// waiting until somebody divides it means the person who mistyped it is not the person
    /// who has to work out what happened.
    /// <para>
    /// Separate from <see cref="RefuseIfAnythingIsUnclaimed"/> for exactly that reason: these
    /// figures must hold from the start, whereas a line nobody has claimed yet is the
    /// ordinary state of a bill somebody is still working through.
    /// </para>
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
    /// Refuses a bill with a line belonging to nobody.
    /// </summary>
    /// <remarks>
    /// Only when it comes to dividing. Storing a bill with unclaimed lines is the whole
    /// point of storing one before it is divided -- somebody scans the paper, and then five
    /// people tap what they had.
    /// </remarks>
    private static void RefuseIfAnythingIsUnclaimed(Receipt receipt)
    {
        // Only the lines that are divided *by* their claims. A line set to Evenly names
        // nobody by design, and is exactly how "the rest is shared" is said -- reading those
        // as unclaimed would refuse the bill the feature exists to allow.
        var unclaimed = receipt.Items
            .Where(item => item.Division == ReceiptItemDivision.Claimed && item.Claims.Count == 0)
            .ToList();

        // Refused rather than spread over everybody. Which of the two a person meant is not
        // knowable from here -- a forgotten line and a line genuinely shared by the table
        // look identical -- and quietly charging five people for a steak one of them ordered
        // is the kind of wrong nobody checks for afterwards. Claiming it explicitly takes one
        // tap.
        if (unclaimed.Count > 0)
            throw new UnprocessableException(ErrorCodes.ReceiptItemsUnclaimed,
                    $"{unclaimed.Count} item(s) on this receipt belong to nobody, so the bill " +
                    "cannot be divided yet.")
                // By name as well as by id, so a client can say which without holding the
                // whole receipt, and a person reading the raw problem can see the answer.
                .WithExtension("unclaimedItemIds", unclaimed.ConvertAll(item => item.Id))
                .WithExtension("unclaimedItemNames", unclaimed.ConvertAll(item => item.Name));
    }

    /// <summary>
    /// The largest scale that keeps every weight inside an <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// A weight is an int, so a claimed subtotal of 214,748.36 is as far as hundredths of a
    /// cent reach. Stepping down to cents, and then to whole units, buys two more orders of
    /// magnitude each -- which is past any bill this will ever meet, but the arithmetic
    /// should degrade rather than fail.
    /// <para>
    /// Nobody's claimed subtotal can exceed the subtotal itself, since a line's price is
    /// refused if negative and is shared out in full, so bounding on the subtotal bounds
    /// every weight.
    /// </para>
    /// </remarks>
    private static int ScaleFor(decimal subtotal)
    {
        foreach (var scale in (ReadOnlySpan<int>)[PreferredScale, 100, 1])
            if (subtotal * scale < int.MaxValue)
                return scale;

        // Its own code, not RECEIPT_DOES_NOT_ADD_UP: the figures on this bill agree with each
        // other perfectly and the client would tell somebody their bill does not add up,
        // which is both wrong and unactionable. Nothing is wrong with it except its size.
        throw new UnprocessableException(ErrorCodes.ReceiptTooLargeToDivide,
                "This receipt is too large to divide by its items.")
            .WithExtension("subtotal", subtotal);
    }
}

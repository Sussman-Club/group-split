using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Where the tax and the tip land, and what the arithmetic does with a bill it cannot take
/// at face value.
/// </summary>
/// <remarks>
/// The gaps a review of this feature found, all of them in the same place: every bill in
/// <c>ReceiptSplitCalculatorTest</c> has one part and every line on it is taxable, so the two
/// rules that decide who pays the extras were never actually put to the question.
/// <para>
/// They are the rules people check. Somebody who ordered the exempt thing and is charged a
/// share of the tax on somebody else's will notice, and there is no screen that would have
/// said so: the balances would simply be a little wrong for everybody.
/// </para>
/// </remarks>
public class ReceiptTaxAndTipTest
{
    private static readonly Guid Alice = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = new("22222222-2222-2222-2222-222222222222");

    private static readonly Guid Part = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Other = new("55555555-5555-5555-5555-555555555555");

    // ---- tax within one part -----------------------------------------------------------

    /// <summary>
    /// Tax follows the lines it was charged on, even when both are in one expense.
    /// </summary>
    /// <remarks>
    /// The only thing keeping tax off an exempt claimant on an ordinary bill, and it was
    /// invisible to the suite: the tested case put the exempt line in a different part, which
    /// exercises the between-parts arm of the same apportioning and not this one.
    /// <para>
    /// Alice had 40.00 of exempt groceries; Bob had 40.00 of taxable clothes carrying all
    /// 8.00 of the tax. Spread over the lines instead, they would owe 44.00 each.
    /// </para>
    /// </remarks>
    [Fact]
    public void Tax_stays_off_the_exempt_line_when_both_are_one_expense()
    {
        var bill = Bill(tax: 8m, tip: 0m,
            (40m, Alice, true),
            (40m, Bob, false));

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        Assert.Equal(48m, Share(shares, Alice));
        Assert.Equal(40m, Share(shares, Bob));
        Assert.Equal(88m, shares.Sum(share => share.Amount));
    }

    /// <summary>
    /// A bill where nothing is taxable spreads its tax over everything instead of losing it.
    /// </summary>
    /// <remarks>
    /// The fallback exists so a transcription error does not make a real bill undividable,
    /// and it is the reason the seed files are checked for a bill that charges tax with every
    /// line marked exempt: the tax would land on the exempt lines and balance, which is the
    /// one outcome <c>IsTaxable</c> was added to prevent.
    /// </remarks>
    [Fact]
    public void Tax_on_a_bill_with_nothing_taxable_spreads_over_every_line()
    {
        var bill = Bill(tax: 10m, tip: 0m,
            (60m, Alice, true),
            (40m, Bob, true));

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        Assert.Equal(66m, Share(shares, Alice));
        Assert.Equal(44m, Share(shares, Bob));
    }

    /// <summary>
    /// The tip is about the bill rather than the goods, so it follows every line -- exempt
    /// ones included.
    /// </summary>
    [Fact]
    public void The_tip_follows_every_line_including_the_exempt_ones()
    {
        var bill = Bill(tax: 0m, tip: 10m,
            (60m, Alice, true),
            (40m, Bob, false));

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        Assert.Equal(66m, Share(shares, Alice));
        Assert.Equal(44m, Share(shares, Bob));
    }

    // ---- the extras across the parts of a split charge ---------------------------------

    /// <summary>
    /// A tip on a split charge is apportioned between its parts in proportion to their lines.
    /// </summary>
    /// <remarks>
    /// No multi-part bill anywhere carried a tip, so this arm of the apportioning had never
    /// run: every split charge in the tests was a supermarket receipt.
    /// </remarks>
    [Fact]
    public void A_tip_on_a_split_charge_is_shared_between_its_parts_by_what_they_came_to()
    {
        var bill = Bill(tax: 0m, tip: 20m,
            (60m, Alice, true, Part),
            (40m, Bob, true, Other));

        var amounts = ReceiptSplitCalculator.PartAmounts(bill);

        Assert.Equal(72m, amounts[Part]);
        Assert.Equal(48m, amounts[Other]);
        Assert.Equal(120m, amounts.Values.Sum());
    }

    /// <summary>
    /// And a tax on one is apportioned over the taxable lines, wherever they sit.
    /// </summary>
    /// <remarks>
    /// The warehouse case, and the reason the whole bill has to be placed before any part of
    /// it is divided: the clothes carry the tax and the groceries do not, so a part's amount
    /// cannot be worked out from its own lines alone.
    /// </remarks>
    [Fact]
    public void Tax_on_a_split_charge_falls_on_the_part_holding_the_taxable_lines()
    {
        var bill = Bill(tax: 10m, tip: 0m,
            (60m, Alice, false, Part),
            (40m, Bob, true, Other));

        var amounts = ReceiptSplitCalculator.PartAmounts(bill);

        Assert.Equal(60m, amounts[Part]);
        Assert.Equal(50m, amounts[Other]);
    }

    /// <summary>
    /// The leftover cent lands on the same part whatever order the lines arrive in.
    /// </summary>
    /// <remarks>
    /// Where the largest part by line value has nothing taxable on it, the tax cannot favour
    /// it and the leftover fell to the first key of a dictionary -- which is insertion order,
    /// which is whatever order the caller's query happened to return the lines in. Two
    /// queries loaded this bill, one ordering by position and one not ordering at all, so
    /// pressing "divide again" could move a cent between two people with nothing on the bill
    /// having changed.
    /// </remarks>
    [Fact]
    public void The_leftover_cent_does_not_depend_on_the_order_the_lines_arrive_in()
    {
        var third = Guid.Parse("66666666-6666-4666-8666-666666666666");

        // 100.00 exempt and largest, then two taxable parts of 30.00 with a cent of tax
        // between them: neither can be favoured, so the leftover has to be placed by rule.
        var forwards = Bill(tax: 0.01m, tip: 0m,
            (100m, Alice, false, Part),
            (30m, Bob, true, Other),
            (30m, Bob, true, third));

        var backwards = Bill(tax: 0.01m, tip: 0m,
            (100m, Alice, false, Part),
            (30m, Bob, true, third),
            (30m, Bob, true, Other));

        Assert.Equal(
            ReceiptSplitCalculator.PartAmounts(forwards).OrderBy(pair => pair.Key),
            ReceiptSplitCalculator.PartAmounts(backwards).OrderBy(pair => pair.Key));
    }

    // ---- what the arithmetic refuses ---------------------------------------------------

    /// <summary>A line nobody claimed stops the part it is in, and only that part.</summary>
    [Fact]
    public void A_line_belonging_to_nobody_stops_its_own_part_and_no_other()
    {
        var bill = Bill(tax: 0m, tip: 0m,
            (60m, Alice, true, Part),
            (40m, (Guid?)null, true, Other));

        Assert.True(ReceiptSplitCalculator.CanDivide(bill, Part));
        Assert.False(ReceiptSplitCalculator.CanDivide(bill, Other));

        var refusal = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(bill, Other, Alice, [Alice, Bob]));

        Assert.Equal(ErrorCodes.ReceiptItemsUnclaimed, refusal.Code);
    }

    /// <summary>
    /// A claim with no weight behind it is refused rather than divided by zero.
    /// </summary>
    /// <remarks>
    /// The weights on a line are its denominator. A zero -- or a negative, which is the same
    /// mistake typed differently -- either divides by nothing or hands somebody a negative
    /// share of a purchase, and both are stored balances nobody can explain.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_claim_weighing_nothing_is_refused(int weight)
    {
        var bill = Bill(tax: 0m, tip: 0m, (60m, Alice, true));

        bill.Items.Single().Claims.Single().Weight = weight;

        Assert.Throws<ValidationException>(
            () => ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]));

        // And the flag a client reads says so rather than lighting up a button that refuses.
        Assert.False(ReceiptSplitCalculator.CanDivide(bill, Part));
    }

    /// <summary>
    /// A bill whose lines do not come to its own subtotal is refused before anything is
    /// apportioned.
    /// </summary>
    /// <remarks>
    /// Checked when the bill is stored as well, but this is the one that matters: it runs at
    /// the head of every division, which is what keeps a receipt somebody edited around the
    /// edges from quietly dividing into shares that do not sum to the charge.
    /// </remarks>
    [Fact]
    public void A_bill_whose_lines_do_not_come_to_its_subtotal_is_refused()
    {
        var bill = Bill(tax: 0m, tip: 0m, (60m, Alice, true));

        bill.Subtotal = 70m;
        bill.Total = 70m;

        var refusal = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refusal.Code);
    }

    /// <summary>And one whose extras do not come to its total.</summary>
    [Fact]
    public void A_bill_whose_extras_do_not_come_to_its_total_is_refused()
    {
        var bill = Bill(tax: 5m, tip: 0m, (60m, Alice, true));

        bill.Total = 70m;

        var refusal = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refusal.Code);
    }

    /// <summary>
    /// A part of nothing but free lines comes to nothing, and says so rather than throwing.
    /// </summary>
    /// <remarks>
    /// A promotional line on a warehouse bill. The arithmetic handles it -- the part's weight
    /// is zero and it takes none of the extras -- and what a caller does about an expense of
    /// 0.00 is the caller's business, not this function's.
    /// </remarks>
    [Fact]
    public void A_part_of_nothing_but_free_lines_comes_to_nothing()
    {
        var bill = Bill(tax: 0m, tip: 0m,
            (60m, Alice, true, Part),
            (0m, Bob, true, Other));

        var amounts = ReceiptSplitCalculator.PartAmounts(bill);

        Assert.Equal(60m, amounts[Part]);
        Assert.Equal(0m, amounts[Other]);
    }

    /// <summary>
    /// One bill. Each line is its price, who had it (null for nobody), whether tax was
    /// charged on it, and which purchase it is part of.
    /// </summary>
    private static Receipt Bill(
        decimal tax, decimal tip,
        params (decimal Price, Guid? Had, bool Taxable, Guid Part)[] lines)
    {
        var subtotal = lines.Sum(line => line.Price);

        var receipt = new Receipt
        {
            Subtotal = subtotal,
            Tax = tax,
            Tip = tip,
            Total = subtotal + tax + tip
        };

        foreach (var (price, had, taxable, part) in lines)
        {
            var item = new ReceiptItem
            {
                Name = $"Line {price}",
                NormalizedName = $"line {price}",
                TotalPrice = price,
                IsTaxable = taxable,
                ExpenseId = part
            };

            if (had is { } user)
                item.Claims.Add(new ReceiptItemClaim { UserId = user, Weight = 1 });

            receipt.Items.Add(item);
        }

        return receipt;
    }

    private static Receipt Bill(
        decimal tax, decimal tip, params (decimal Price, Guid? Had, bool Taxable)[] lines) =>
        Bill(tax, tip, [.. lines.Select(line => (line.Price, line.Had, line.Taxable, Part))]);

    private static decimal Share(IReadOnlyList<SplitAmount> shares, Guid userId) =>
        shares.SingleOrDefault(share => share.UserId == userId).Amount;
}

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
/// Tax is on the lines and is never apportioned: a part's tax is its own lines' tax, and a
/// person's is the tax of the lines they claimed. The tip belongs to no line -- nothing on
/// the paper says whose it was -- so it is the one figure that gets spread.
/// <para>
/// It was the other way round, and wrong for it. A single tax total was weighed over the lines
/// a boolean marked as taxed, in proportion to their prices, which is exact only where every
/// taxed line carries one rate. A Portuguese supermarket receipt does not, and the people who
/// bought the 6% food paid part of the 23% on somebody else's household goods -- with the
/// total still adding up, so no screen anywhere could have said so.
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
    /// Two rates on one bill, and each person pays the tax on what they actually bought.
    /// </summary>
    /// <remarks>
    /// The case the whole change is for. 100.00 of food at 6% and 100.00 of household goods
    /// at 23% is 29.00 of tax; weighed by price it came out 14.50 each, so whoever had only
    /// the food was overcharged 8.50 and whoever had the rest was let off the same.
    /// </remarks>
    [Fact]
    public void Two_rates_on_one_bill_land_on_the_people_who_bought_them()
    {
        var bill = Bill(tip: 0m,
            (100m, Alice, 6m),
            (100m, Bob, 23m));

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        Assert.Equal(106m, Share(shares, Alice));
        Assert.Equal(123m, Share(shares, Bob));
        Assert.Equal(229m, shares.Sum(share => share.Amount));
    }

    /// <summary>
    /// And a line carrying no tax carries none of anybody else's.
    /// </summary>
    /// <remarks>
    /// The exempt-groceries case, which the boolean did get right on a single-rate bill. It
    /// is the same arithmetic here with a rate of nothing.
    /// </remarks>
    [Fact]
    public void A_line_that_carried_no_tax_pays_none()
    {
        var bill = Bill(tip: 0m,
            (40m, Alice, 0m),
            (40m, Bob, 8m));

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        Assert.Equal(40m, Share(shares, Alice));
        Assert.Equal(48m, Share(shares, Bob));
    }

    /// <summary>
    /// Sharing a line shares what it was taxed, in the same proportion.
    /// </summary>
    /// <remarks>
    /// A claim is a claim on the whole line: half the steak is half of what the steak cost and
    /// half of what it was taxed. Nothing else would be defensible -- the alternative weighs
    /// the part's tax over people by what their lines cost, which on a mixed-rate bill is a
    /// different figure.
    /// </remarks>
    [Fact]
    public void Two_people_sharing_a_line_share_its_tax_the_same_way()
    {
        var bill = Bill(tip: 0m, (100m, Alice, 23m));

        bill.Items.Single().Claims.Add(new ReceiptItemClaim { UserId = Bob, Weight = 3 });

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        // A quarter of the line and a quarter of its tax.
        Assert.Equal(30.75m, Share(shares, Alice));
        Assert.Equal(92.25m, Share(shares, Bob));
    }

    /// <summary>
    /// The tip is about the bill rather than the goods, so it follows every line.
    /// </summary>
    [Fact]
    public void The_tip_follows_every_line_including_the_untaxed_ones()
    {
        var bill = Bill(tip: 10m,
            (60m, Alice, 0m),
            (40m, Bob, 0m));

        var shares = ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]);

        Assert.Equal(66m, Share(shares, Alice));
        Assert.Equal(44m, Share(shares, Bob));
    }

    // ---- the extras across the parts of a split charge ---------------------------------

    /// <summary>
    /// A tip on a split charge is shared between its parts by what their lines came to.
    /// </summary>
    /// <remarks>
    /// No multi-part bill anywhere carried a tip, so this arm had never run: every split
    /// charge in the tests was a supermarket receipt.
    /// </remarks>
    [Fact]
    public void A_tip_on_a_split_charge_is_shared_between_its_parts_by_what_they_came_to()
    {
        var bill = Bill(tip: 20m,
            (60m, Alice, 0m, Part),
            (40m, Bob, 0m, Other));

        var amounts = ReceiptSplitCalculator.PartAmounts(bill);

        Assert.Equal(72m, amounts[Part]);
        Assert.Equal(48m, amounts[Other]);
        Assert.Equal(120m, amounts.Values.Sum());
    }

    /// <summary>
    /// And the tax on one is simply the tax of each part's own lines.
    /// </summary>
    /// <remarks>
    /// The warehouse case: the clothes carry the tax and the groceries do not. It used to need
    /// the whole bill placed before any part of it could be priced, because a part's share of
    /// one tax total depended on what the other parts held. It does not any more -- but the
    /// placement still has to come first, because the tip is still spread.
    /// </remarks>
    [Fact]
    public void Each_part_of_a_split_charge_carries_its_own_lines_tax()
    {
        var bill = Bill(tip: 0m,
            (60m, Alice, 0m, Part),
            (40m, Bob, 10m, Other));

        var amounts = ReceiptSplitCalculator.PartAmounts(bill);

        Assert.Equal(60m, amounts[Part]);
        Assert.Equal(50m, amounts[Other]);
    }

    /// <summary>
    /// The tip's leftover cent lands on the same part whatever order the lines arrive in.
    /// </summary>
    /// <remarks>
    /// The one figure still spread, so the one that can still have a leftover. It fell to the
    /// first key of a dictionary -- insertion order, which is whatever order the caller's
    /// query returned the lines in. Two queries load this bill, one ordering by position and
    /// one that did not order at all, so pressing "divide again" moved a cent between two
    /// people with nothing on the bill having changed.
    /// </remarks>
    [Fact]
    public void The_leftover_cent_does_not_depend_on_the_order_the_lines_arrive_in()
    {
        var third = Guid.Parse("66666666-6666-4666-8666-666666666666");

        var forwards = Bill(tip: 0.01m,
            (100m, Alice, 0m, Part),
            (30m, Bob, 0m, Other),
            (30m, Bob, 0m, third));

        var backwards = Bill(tip: 0.01m,
            (100m, Alice, 0m, Part),
            (30m, Bob, 0m, third),
            (30m, Bob, 0m, Other));

        Assert.Equal(
            ReceiptSplitCalculator.PartAmounts(forwards).OrderBy(pair => pair.Key),
            ReceiptSplitCalculator.PartAmounts(backwards).OrderBy(pair => pair.Key));
    }

    // ---- what the arithmetic refuses ---------------------------------------------------

    /// <summary>
    /// A bill charging tax that none of its lines accounts for is refused.
    /// </summary>
    /// <remarks>
    /// This is what replaced a silent fallback. The tax used to be one figure spread over
    /// whatever a boolean marked, and a bill charging tax while marking every line exempt
    /// spread it over everything instead and balanced -- arriving at the one outcome the flag
    /// existed to prevent, with no error anywhere. Now the lines have to come to the figure at
    /// the bottom of the paper, and a bill that cannot say where its tax was charged is a
    /// transcription that is not finished.
    /// </remarks>
    [Fact]
    public void A_bill_whose_lines_do_not_account_for_its_tax_is_refused()
    {
        var bill = Bill(tip: 0m, (60m, Alice, 0m), (40m, Bob, 0m));

        bill.Tax = 10m;
        bill.Total = 110m;

        var refusal = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refusal.Code);
        Assert.Contains("tax on the lines", refusal.Message);
    }

    /// <summary>A line nobody claimed stops the part it is in, and only that part.</summary>
    [Fact]
    public void A_line_belonging_to_nobody_stops_its_own_part_and_no_other()
    {
        var bill = Bill(tip: 0m,
            (60m, Alice, 0m, Part),
            (40m, null, 0m, Other));

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
        var bill = Bill(tip: 0m, (60m, Alice, 0m));

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
    [Fact]
    public void A_bill_whose_lines_do_not_come_to_its_subtotal_is_refused()
    {
        var bill = Bill(tip: 0m, (60m, Alice, 0m));

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
        var bill = Bill(tip: 0m, (60m, Alice, 5m));

        bill.Total = 70m;

        var refusal = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(bill, Part, Alice, [Alice, Bob]));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, refusal.Code);
    }

    /// <summary>
    /// A part of nothing but free lines comes to nothing, and says so rather than throwing.
    /// </summary>
    [Fact]
    public void A_part_of_nothing_but_free_lines_comes_to_nothing()
    {
        var bill = Bill(tip: 0m,
            (60m, Alice, 0m, Part),
            (0m, Bob, 0m, Other));

        var amounts = ReceiptSplitCalculator.PartAmounts(bill);

        Assert.Equal(60m, amounts[Part]);
        Assert.Equal(0m, amounts[Other]);
    }

    /// <summary>
    /// One bill. Each line is its price, who had it (null for nobody), what of the bill's tax
    /// it carried, and which purchase it is part of.
    /// </summary>
    /// <remarks>
    /// The receipt's own tax is the lines' added up, which is now an invariant rather than a
    /// convenience: a fixture that stated both could state a bill the arithmetic refuses.
    /// </remarks>
    private static Receipt Bill(
        decimal tip, params (decimal Price, Guid? Had, decimal Tax, Guid Part)[] lines)
    {
        var subtotal = lines.Sum(line => line.Price);
        var tax = lines.Sum(line => line.Tax);

        var receipt = new Receipt
        {
            Subtotal = subtotal,
            Tax = tax,
            Tip = tip,
            Total = subtotal + tax + tip
        };

        foreach (var (price, had, lineTax, part) in lines)
        {
            var item = new ReceiptItem
            {
                Name = $"Line {price}",
                NormalizedName = $"line {price}",
                TotalPrice = price,
                TaxAmount = lineTax,
                ExpenseId = part
            };

            if (had is { } user)
                item.Claims.Add(new ReceiptItemClaim { UserId = user, Weight = 1 });

            receipt.Items.Add(item);
        }

        return receipt;
    }

    private static Receipt Bill(
        decimal tip, params (decimal Price, Guid? Had, decimal Tax)[] lines) =>
        Bill(tip, [.. lines.Select(line => (line.Price, line.Had, line.Tax, Part))]);

    private static decimal Share(IReadOnlyList<SplitAmount> shares, Guid userId) =>
        shares.SingleOrDefault(share => share.UserId == userId).Amount;
}

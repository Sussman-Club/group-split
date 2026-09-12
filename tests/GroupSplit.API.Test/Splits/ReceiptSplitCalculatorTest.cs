using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// Dividing a bill by who had what.
/// </summary>
/// <remarks>
/// The invariant every case here asserts, whatever else it is about, is that the shares sum
/// to the receipt's total. That is what the group's balances rest on, and an itemised split
/// has more ways to lose a cent than any other kind: a line shared three ways, a tip
/// apportioned across five people, and a remainder that has to land somewhere.
/// </remarks>
public class ReceiptSplitCalculatorTest
{
    private static readonly Guid Alice = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carol = new("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// The expense every line here belongs to. A restaurant bill is one purchase, so it is
    /// one part -- which is the shape this whole file is about, and the reason splitting a
    /// warehouse charge into two changed none of it.
    /// </summary>
    private static readonly Guid Part = new("44444444-4444-4444-4444-444444444444");

    /// <param name="lines">
    /// Each line as its price and who had it, with a weight apiece. The subtotal and the
    /// total are worked out from the lines and the extras rather than passed, because a
    /// receipt that does not add up is refused and almost every case here wants one that
    /// does.
    /// </param>
    private static Receipt Bill(decimal tax, decimal tip, params (decimal Price, (Guid User, int Weight)[] Had)[] lines)
    {
        var subtotal = lines.Sum(line => line.Price);

        var receipt = new Receipt { Subtotal = subtotal, Total = subtotal + tax + tip, Tax = tax, Tip = tip };

        foreach (var (price, had) in lines)
        {
            var item = new ReceiptItem
            {
                Name = $"Line {price}",
                NormalizedName = $"line {price}",
                TotalPrice = price,
                ExpenseId = Part
            };

            foreach (var (user, weight) in had)
                item.Claims.Add(new ReceiptItemClaim { UserId = user, Weight = weight });

            receipt.Items.Add(item);
        }

        return receipt;
    }

    private static (Guid, int)[] Had(params Guid[] users) => [.. users.Select(user => (user, 1))];

    /// <summary>
    /// Everybody the expense could be divided between, for the lines that name nobody.
    /// </summary>
    private static readonly Guid[] Everyone = [Alice, Bob, Carol];

    /// <summary>Marks a line as the table's rather than anybody's in particular.</summary>
    private static Receipt Shared(Receipt receipt, int index)
    {
        receipt.Items.ElementAt(index).Division = ReceiptItemDivision.Evenly;

        return receipt;
    }

    private static decimal AmountFor(IReadOnlyList<SplitAmount> splits, Guid userId) =>
        splits.SingleOrDefault(split => split.UserId == userId).Amount;

    [Fact]
    public void Each_person_owes_what_they_claimed()
    {
        var receipt = Bill(tax: 0m, tip: 0m,
            (30.00m, Had(Alice)),
            (20.00m, Had(Bob)));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(30.00m, AmountFor(splits, Alice));
        Assert.Equal(20.00m, AmountFor(splits, Bob));
        Assert.Equal(50.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// The case the whole feature exists for: the tax and the tip follow what somebody ate,
    /// not the number of people at the table.
    /// </summary>
    [Fact]
    public void Tax_and_tip_are_apportioned_by_what_each_person_claimed()
    {
        // Alice has three quarters of the food, so she owes three quarters of the 20.00 that
        // is not food. An even split of the extras would have charged them 10.00 each.
        var receipt = Bill(tax: 8.00m, tip: 12.00m,
            (75.00m, Had(Alice)),
            (25.00m, Had(Bob)));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(90.00m, AmountFor(splits, Alice));
        Assert.Equal(30.00m, AmountFor(splits, Bob));
        Assert.Equal(120.00m, splits.Sum(split => split.Amount));
    }

    [Fact]
    public void A_line_two_people_shared_is_split_between_them()
    {
        var receipt = Bill(tax: 0m, tip: 0m,
            (10.00m, Had(Alice)),
            (18.00m, Had(Alice, Bob)));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(19.00m, AmountFor(splits, Alice));
        Assert.Equal(9.00m, AmountFor(splits, Bob));
    }

    [Fact]
    public void A_weight_says_somebody_had_more_of_a_line()
    {
        var receipt = Bill(tax: 0m, tip: 0m, (30.00m, [(Alice, 2), (Bob, 1)]));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(20.00m, AmountFor(splits, Alice));
        Assert.Equal(10.00m, AmountFor(splits, Bob));
    }

    /// <summary>
    /// A third of a bottle is not a number of cents, and three of them do not make a whole
    /// one. The payer carries what is left over, exactly as in every other division.
    /// </summary>
    [Fact]
    public void A_line_that_does_not_divide_evenly_leaves_the_remainder_with_the_payer()
    {
        var receipt = Bill(tax: 0m, tip: 0m, (10.00m, Had(Alice, Bob, Carol)));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(10.00m, splits.Sum(split => split.Amount));
        Assert.Equal(3.34m, AmountFor(splits, Alice));
        Assert.Equal(3.33m, AmountFor(splits, Bob));
        Assert.Equal(3.33m, AmountFor(splits, Carol));
    }

    /// <summary>
    /// Apportioning the extras is where the thirds compound, and the sum still has to come
    /// out exactly. The one property that must hold for any bill at all.
    /// </summary>
    [Theory]
    [InlineData(0.01, 0)]
    [InlineData(1.37, 2.11)]
    [InlineData(0, 9.99)]
    [InlineData(13.33, 6.67)]
    public void The_shares_always_sum_to_the_total(double tax, double tip)
    {
        var receipt = Bill((decimal)tax, (decimal)tip,
            (10.01m, Had(Alice, Bob, Carol)),
            (7.77m, Had(Bob)),
            (0.03m, [(Alice, 2), (Carol, 1)]));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Bob, Everyone);

        Assert.Equal(receipt.Total, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// Refused rather than spread over everybody: a forgotten line and one the table really
    /// did share look identical from here.
    /// </summary>
    [Fact]
    public void A_line_nobody_claimed_refuses_the_whole_bill()
    {
        var receipt = Bill(tax: 0m, tip: 0m,
            (10.00m, Had(Alice)),
            (22.00m, []));

        var thrown = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone));

        Assert.Equal(ErrorCodes.ReceiptItemsUnclaimed, thrown.Code);
        Assert.False(ReceiptSplitCalculator.CanDivide(receipt, Part));
    }

    [Fact]
    public void A_bill_whose_parts_do_not_reach_its_total_is_refused()
    {
        var receipt = Bill(tax: 0m, tip: 0m, (10.00m, Had(Alice)));

        // The kind of slip that arrives from a mistyped total rather than from bad claims.
        receipt.Total = 12.00m;

        var thrown = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, thrown.Code);
    }

    [Fact]
    public void A_bill_whose_lines_do_not_reach_its_subtotal_is_refused()
    {
        var receipt = Bill(tax: 0m, tip: 0m, (10.00m, Had(Alice)));

        receipt.Subtotal = 9.00m;
        receipt.Total = 9.00m;

        var thrown = Assert.Throws<UnprocessableException>(
            () => ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone));

        Assert.Equal(ErrorCodes.ReceiptDoesNotAddUp, thrown.Code);
    }

    /// <summary>
    /// Comped food with a tip left on it. In proportion to what you had degenerates when
    /// nobody had anything priced, and evenly between whoever was there is the only answer
    /// that does not invent an order.
    /// </summary>
    [Fact]
    public void A_bill_of_free_lines_with_a_tip_is_divided_evenly_between_the_claimants()
    {
        var receipt = Bill(tax: 0m, tip: 9.00m,
            (0.00m, Had(Alice)),
            (0.00m, Had(Bob)),
            (0.00m, Had(Carol)));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(3.00m, AmountFor(splits, Alice));
        Assert.Equal(3.00m, AmountFor(splits, Bob));
        Assert.Equal(3.00m, AmountFor(splits, Carol));
    }

    [Fact]
    public void Somebody_who_claimed_nothing_gets_no_share()
    {
        var receipt = Bill(tax: 5.00m, tip: 5.00m,
            (40.00m, Had(Alice)),
            (10.00m, Had(Bob)));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        // Not a zero row: a bill names who ate, and Carol was not there. The distinction
        // matters because a zero split would put her in the group's balances for this
        // expense.
        Assert.DoesNotContain(splits, split => split.UserId == Carol);
    }

    /// <summary>
    /// A validation refusal and not an unprocessable one, so that RECEIPT_INVALID means 400
    /// wherever it comes from. The wire refuses this with [Range]; what reaches here is a row
    /// written past the API, and a code answering two statuses is one a client cannot branch
    /// on.
    /// </summary>
    [Fact]
    public void A_claim_with_no_share_in_it_is_refused()
    {
        var receipt = Bill(tax: 0m, tip: 0m, (10.00m, [(Alice, 0)]));

        var thrown = Assert.Throws<ValidationException>(
            () => ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone));

        Assert.Equal(ErrorCodes.ReceiptInvalid, thrown.Code);
    }

    /// <summary>
    /// The shape the per-line division exists for: a couple of things were somebody's and the
    /// rest was the table's.
    /// </summary>
    [Fact]
    public void A_line_set_to_evenly_is_shared_between_everybody_without_naming_them()
    {
        var receipt = Shared(Bill(tax: 0m, tip: 0m,
            (30.00m, Had(Alice)),
            (30.00m, [])), 1);

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        // 30.00 of her own, plus a third of the shared 30.00.
        Assert.Equal(40.00m, AmountFor(splits, Alice));
        Assert.Equal(10.00m, AmountFor(splits, Bob));
        Assert.Equal(10.00m, AmountFor(splits, Carol));
        Assert.Equal(60.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A line that names nobody by design must not be read as one somebody forgot to claim,
    /// which would refuse the bill the feature exists to allow.
    /// </summary>
    [Fact]
    public void A_line_shared_by_everybody_is_not_an_unclaimed_line()
    {
        var receipt = Shared(Shared(Bill(tax: 2.00m, tip: 3.00m,
            (20.00m, []),
            (10.00m, [])), 0), 1);

        Assert.True(ReceiptSplitCalculator.CanDivide(receipt, Part));

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);

        Assert.Equal(35.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// What the roster is for. The same bill divides between whoever is actually there, which
    /// is the difference from claiming the line for everybody by hand.
    /// </summary>
    [Fact]
    public void A_shared_line_follows_who_is_there_rather_than_who_was_named()
    {
        var receipt = Shared(Bill(tax: 0m, tip: 0m, (30.00m, [])), 0);

        var betweenThree = ReceiptSplitCalculator.Divide(receipt, Part, Alice, Everyone);
        var betweenTwo = ReceiptSplitCalculator.Divide(receipt, Part, Alice, [Alice, Bob]);

        Assert.Equal(10.00m, AmountFor(betweenThree, Bob));
        Assert.Equal(15.00m, AmountFor(betweenTwo, Bob));
        Assert.DoesNotContain(betweenTwo, split => split.UserId == Carol);
    }

    /// <summary>
    /// Mixing the kinds still rounds once: the parts come to the total exactly.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.37, 2.11)]
    [InlineData(0.01, 9.99)]
    public void Claimed_and_shared_lines_together_still_sum_to_the_total(double tax, double tip)
    {
        var receipt = Shared(Shared(Bill((decimal)tax, (decimal)tip,
            (10.01m, Had(Alice)),
            (7.77m, []),
            (0.03m, [])), 1), 2);

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Bob, Everyone);

        Assert.Equal(receipt.Total, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// Tax and tip follow the whole of what somebody had, however the lines that gave it to
    /// them were divided.
    /// </summary>
    [Fact]
    public void The_extras_follow_shares_that_came_from_lines_nobody_was_named_on()
    {
        // Alice: 60.00 of her own. Bob and Carol: 20.00 each of the shared 40.00 -- so the
        // 20.00 of extras splits 60:20:20, which is 12.00 / 4.00 / 4.00.
        var receipt = Shared(Bill(tax: 8.00m, tip: 12.00m,
            (60.00m, Had(Alice)),
            (40.00m, [])), 1);

        var splits = ReceiptSplitCalculator.Divide(receipt, Part, Alice, [Bob, Carol]);

        Assert.Equal(72.00m, AmountFor(splits, Alice));
        Assert.Equal(24.00m, AmountFor(splits, Bob));
        Assert.Equal(24.00m, AmountFor(splits, Carol));
        Assert.Equal(120.00m, splits.Sum(split => split.Amount));
    }

    [Fact]
    public void A_fully_claimed_bill_that_adds_up_can_be_divided()
    {
        var receipt = Bill(tax: 1.00m, tip: 2.00m, (10.00m, Had(Alice, Bob)));

        Assert.True(ReceiptSplitCalculator.CanDivide(receipt, Part));
    }
}

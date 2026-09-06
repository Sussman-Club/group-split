using GroupSplit.API.Services;
namespace GroupSplit.API.Test.Splits;

/// <summary>
/// The split arithmetic, which is now written once and therefore has to be right once.
/// </summary>
/// <remarks>
/// Two properties carry nearly all of this: the parts sum to the whole, and the remainder
/// goes to the payer. Everything else is a case where one of the two used to fail.
/// </remarks>
public class SplitCalculatorTest
{
    // Ordered so that `alice` sorts first and `carol` last, because two of the tests below
    // are about a tie being broken by id rather than by enumeration order.
    private static readonly Guid Alice = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carol = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Dave = new("44444444-4444-4444-4444-444444444444");

    private static decimal AmountFor(IReadOnlyList<SplitAmount> splits, Guid userId) =>
        splits.Single(split => split.UserId == userId).Amount;

    [Fact]
    public void An_even_split_that_does_not_divide_gives_the_payer_the_remainder()
    {
        var splits = SplitCalculator.DivideEvenly(100.00m, Bob, [Alice, Bob, Carol]);

        Assert.Equal(33.33m, AmountFor(splits, Alice));
        Assert.Equal(33.34m, AmountFor(splits, Bob));
        Assert.Equal(33.33m, AmountFor(splits, Carol));
        Assert.Equal(100.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// The regression test for the bug. The old shares handler gave the rounding drift to
    /// `calculated[^1]` -- the last participant the dictionary enumerated -- so who
    /// absorbed the extra cent depended on the order the members arrived in.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_remainder_follows_the_payer_and_not_the_order(int payerPosition)
    {
        var participants = new[] { Alice, Bob, Carol };
        var payer = participants[payerPosition];

        var splits = SplitCalculator.DivideEvenly(100.00m, payer, participants);

        Assert.Equal(33.34m, AmountFor(splits, payer));
        Assert.All(
            splits.Where(split => split.UserId != payer),
            split => Assert.Equal(33.33m, split.Amount));
    }

    /// <summary>
    /// The same split, with the participants handed over in every order, is the same
    /// split. This is what "deterministic" has to mean to be worth anything.
    /// </summary>
    [Fact]
    public void Reordering_the_participants_does_not_move_a_cent()
    {
        var orderings = new[]
        {
            new[] { Alice, Bob, Carol },
            [Carol, Alice, Bob],
            [Bob, Carol, Alice]
        };

        var results = orderings
            .Select(order => SplitCalculator.DivideEvenly(100.00m, Carol, order)
                .OrderBy(split => split.UserId)
                .Select(split => split.Amount)
                .ToArray())
            .ToList();

        Assert.All(results, amounts => Assert.Equal(results[0], amounts));
    }

    [Fact]
    public void Shares_that_divide_exactly_leave_no_remainder_to_place()
    {
        var splits = SplitCalculator.Divide(100.00m, Alice, [
            new SplitWeight(Alice, 2),
            new SplitWeight(Bob, 1),
            new SplitWeight(Carol, 1)
        ]);

        Assert.Equal(50.00m, AmountFor(splits, Alice));
        Assert.Equal(25.00m, AmountFor(splits, Bob));
        Assert.Equal(25.00m, AmountFor(splits, Carol));
    }

    /// <summary>
    /// Weights as hundredths of a percent, which is how a percent rule is stored: 33.33%
    /// twice and 33.34% once still has to land on the amount exactly.
    /// </summary>
    [Fact]
    public void Percentage_weights_sum_to_the_amount()
    {
        var splits = SplitCalculator.Divide(220.50m, Alice, [
            new SplitWeight(Alice, 3333),
            new SplitWeight(Bob, 3333),
            new SplitWeight(Carol, 3334)
        ]);

        Assert.Equal(220.50m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// An amount too small to divide. Everybody else truncates to nothing and the payer
    /// carries the whole penny, which is the only way the parts can still sum to it.
    /// </summary>
    [Fact]
    public void A_penny_four_ways_is_all_the_payers()
    {
        var splits = SplitCalculator.DivideEvenly(0.01m, Bob, [Alice, Bob, Carol, Dave]);

        Assert.Equal(0.01m, AmountFor(splits, Bob));
        Assert.All(
            splits.Where(split => split.UserId != Bob),
            split => Assert.Equal(0m, split.Amount));
    }

    [Fact]
    public void A_participant_weighted_zero_owes_nothing()
    {
        var splits = SplitCalculator.Divide(90.00m, Alice, [
            new SplitWeight(Alice, 1),
            new SplitWeight(Bob, 1),
            new SplitWeight(Carol, 0)
        ]);

        Assert.Equal(0m, AmountFor(splits, Carol));
        Assert.Equal(45.00m, AmountFor(splits, Alice));
        Assert.Equal(45.00m, AmountFor(splits, Bob));
    }

    /// <summary>
    /// A payer excluded from their own split -- they paid for something that is not
    /// theirs to share -- still has to leave the remainder somewhere fixed.
    /// </summary>
    [Fact]
    public void A_payer_outside_the_split_leaves_the_remainder_on_the_largest_weight()
    {
        var splits = SplitCalculator.Divide(100.00m, Dave, [
            new SplitWeight(Alice, 1),
            new SplitWeight(Bob, 1),
            new SplitWeight(Carol, 2)
        ]);

        Assert.Equal(100.00m, splits.Sum(split => split.Amount));
        Assert.Equal(50.00m, AmountFor(splits, Carol));
        Assert.DoesNotContain(splits, split => split.UserId == Dave);
    }

    /// <summary>
    /// The same, where the largest weight is tied: the smaller id wins, so the answer does
    /// not depend on which of them was listed first.
    /// </summary>
    [Fact]
    public void A_tie_for_the_remainder_is_broken_by_id()
    {
        var forwards = SplitCalculator.Divide(100.00m, Dave, [
            new SplitWeight(Alice, 1), new SplitWeight(Bob, 1), new SplitWeight(Carol, 1)
        ]);

        var backwards = SplitCalculator.Divide(100.00m, Dave, [
            new SplitWeight(Carol, 1), new SplitWeight(Bob, 1), new SplitWeight(Alice, 1)
        ]);

        Assert.Equal(33.34m, AmountFor(forwards, Alice));
        Assert.Equal(33.34m, AmountFor(backwards, Alice));
    }

    /// <summary>
    /// The property on its own, over amounts chosen to truncate badly against two, three
    /// and seven ways. If this ever fails, a balance is wrong somewhere.
    /// </summary>
    [Theory]
    [InlineData(0.01)]
    [InlineData(0.02)]
    [InlineData(10.00)]
    [InlineData(33.33)]
    [InlineData(100.00)]
    [InlineData(220.50)]
    [InlineData(999999.99)]
    public void The_parts_always_sum_to_the_whole(decimal amount)
    {
        foreach (var count in Enumerable.Range(1, 7))
        {
            var participants = new[] { Alice, Bob, Carol, Dave }
                .Concat(Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()))
                .Take(count)
                .ToArray();

            var splits = SplitCalculator.DivideEvenly(amount, participants[0], participants);

            Assert.Equal(amount, splits.Sum(split => split.Amount));
        }
    }

    /// <summary>
    /// A refund or a corrected expense. Truncation toward zero means the parts still sum,
    /// and the payer still carries what is left over rather than a member discovering it.
    /// </summary>
    [Fact]
    public void A_negative_amount_still_sums_to_itself()
    {
        var splits = SplitCalculator.DivideEvenly(-100.00m, Bob, [Alice, Bob, Carol]);

        Assert.Equal(-100.00m, splits.Sum(split => split.Amount));
        Assert.Equal(-33.34m, AmountFor(splits, Bob));
    }

    [Fact]
    public void Zero_costs_everybody_nothing()
    {
        var splits = SplitCalculator.DivideEvenly(0m, Alice, [Alice, Bob]);

        Assert.All(splits, split => Assert.Equal(0m, split.Amount));
    }

    [Theory]
    [MemberData(nameof(SplitsThatCouldNotBeStored))]
    public void A_split_that_could_not_be_stored_is_refused(SplitWeight[] weights)
    {
        Assert.Throws<ArgumentException>(() => SplitCalculator.Divide(10.00m, Alice, weights));
    }

    public static TheoryData<SplitWeight[]> SplitsThatCouldNotBeStored() => new()
    {
        { [] },
        { [new SplitWeight(Alice, 1), new SplitWeight(Alice, 1)] },
        { [new SplitWeight(Alice, -1), new SplitWeight(Bob, 2)] },
        { [new SplitWeight(Alice, 0), new SplitWeight(Bob, 0)] }
    };
}

using GroupSplit.App.Shared.Services;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The arithmetic the forms check a division with, which has to be the API's or the forms
/// say yes to what the API refuses (#323).
/// </summary>
public class ShareRoundingTest
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();

    private static SplitInput[] Shares(decimal ana, decimal omar) =>
        [new() { UserId = Ana, Amount = ana }, new() { UserId = Omar, Amount = omar }];

    /// <summary>
    /// Each percentage is stored in whole hundredths, half away from zero, before anything is
    /// added -- the API's <c>PercentSplitRuleHandler</c> -- so the total is of those.
    /// </summary>
    [Theory]
    [InlineData(new[] { 33.333, 33.333, 33.334 }, 99.99)]
    [InlineData(new[] { 33.335, 33.335, 33.33 }, 100.01)]
    [InlineData(new[] { 33.34, 33.33, 33.33 }, 100.00)]
    [InlineData(new[] { 50.0, 50.0 }, 100.00)]
    public void A_percent_total_is_of_each_part_rounded_first(double[] parts, double total)
    {
        var percentages = parts.Select(part => (decimal)part);

        Assert.Equal((decimal)total, ShareRounding.PercentTotal(percentages));
    }

    [Fact]
    public void Shares_that_add_up_in_whole_cents_are_a_division()
    {
        Assert.Null(ShareRounding.Unbalanced(100m, Shares(70m, 30m)));
    }

    [Fact]
    public void No_shares_at_all_is_not_a_division_to_check()
    {
        Assert.Null(ShareRounding.Unbalanced(100m, null));
    }

    [Fact]
    public void Shares_short_of_the_amount_say_what_is_left()
    {
        Assert.Equal("$20.00 still to assign.", ShareRounding.Unbalanced(120m, Shares(70m, 30m)));
    }

    [Fact]
    public void Shares_over_the_amount_say_by_how_much()
    {
        Assert.Equal("$10.00 more than the expense comes to.", ShareRounding.Unbalanced(90m, Shares(70m, 30m)));
    }

    /// <summary>
    /// Counted to the cent, as the fields show them: three shares of 33.333 read 33.33 each,
    /// and what the line says is left is the cent those figures are short.
    /// </summary>
    [Fact]
    public void A_third_decimal_is_counted_as_it_is_shown()
    {
        SplitInput[] thirds =
        [
            new() { UserId = Ana, Amount = 33.333m },
            new() { UserId = Omar, Amount = 33.333m },
            new() { UserId = Guid.NewGuid(), Amount = 33.334m }
        ];

        Assert.Equal("$0.01 still to assign.", ShareRounding.Unbalanced(100m, thirds));
    }

    /// <summary>
    /// And refused even where the rounded figures do add up, because the API refuses a third
    /// decimal place on a share rather than rounding it.
    /// </summary>
    [Fact]
    public void A_third_decimal_is_refused_even_when_the_figures_add_up()
    {
        Assert.Equal("A share can only be whole cents.", ShareRounding.Unbalanced(100m, Shares(33.333m, 66.667m)));
    }
}

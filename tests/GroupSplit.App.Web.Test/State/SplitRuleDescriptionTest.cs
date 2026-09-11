using GroupSplit.App.Shared.Extensions;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// Saying what a division does, in a phrase and in a row of weights.
/// </summary>
/// <remarks>
/// Four screens print the same sentence about the same object -- a category row, a rule row,
/// a version in a rule's history, and the options that correct which version an expense
/// records -- and the one written first said "By shares, between 3". True, and no use to
/// somebody asking who was on what. These pin the shape of the replacement, including the
/// two things it is easy to get wrong: the order, which a dictionary does not have, and the
/// proportions, which are what make 2, 2, 1 legible as 40/40/20.
/// </remarks>
public class SplitRuleDescriptionTest
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Lu = Guid.NewGuid();
    private static readonly Guid Marta = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [Ana] = "Ana",
        [Lu] = "Lu",
        [Marta] = "Marta"
    };

    [Fact]
    public void Shares_are_named_and_listed_heaviest_first()
    {
        var shares = new SharesSplitRuleDto { Shares = { [Marta] = 1, [Ana] = 2, [Lu] = 2 } };

        // Heaviest first whatever order the dictionary happens to hold, so the same rule
        // reads the same way twice.
        Assert.StartsWith("By shares — ", shares.Summary(Names), StringComparison.Ordinal);
        Assert.Contains("Marta 1", shares.Summary(Names), StringComparison.Ordinal);
        Assert.EndsWith("Marta 1", shares.Summary(Names), StringComparison.Ordinal);
    }

    /// <summary>
    /// What the weights come to, which is the figure that makes an edit legible: 2, 2, 1
    /// becoming 1, 1, 1 does not look like a change to anybody's position, and 40% becoming
    /// 33.3% does.
    /// </summary>
    [Fact]
    public void Shares_carry_both_what_was_typed_and_what_it_comes_to()
    {
        var shares = new SharesSplitRuleDto { Shares = { [Ana] = 2, [Lu] = 2, [Marta] = 1 } };

        var weights = shares.Weights(Names);

        Assert.Equal(3, weights.Count);

        var marta = weights.Single(weight => weight.UserId == Marta);

        Assert.Equal("1 share", marta.Held);
        Assert.Equal(20m, marta.Share);

        Assert.Equal("2 shares", weights.Single(weight => weight.UserId == Ana).Held);
        Assert.Equal(40m, weights.Single(weight => weight.UserId == Ana).Share);
    }

    /// <summary>A rule half-typed into a form holds no shares at all, and divides nothing.</summary>
    [Fact]
    public void Shares_that_are_all_zero_have_no_proportion_rather_than_a_division_by_zero()
    {
        var shares = new SharesSplitRuleDto { Shares = { [Ana] = 0, [Lu] = 0 } };

        Assert.All(shares.Weights(Names), weight => Assert.Null(weight.Share));
    }

    [Fact]
    public void Percentages_are_named_and_trailing_zeroes_dropped()
    {
        var percent = new PercentSplitRuleDto { Percentages = { [Ana] = 60.00m, [Lu] = 40.00m } };

        Assert.Equal("By percentage — Ana 60%, Lu 40%", percent.Summary(Names));
    }

    /// <summary>
    /// An even split among nobody in particular is the whole group, and it stays that way
    /// when somebody joins -- which is worth saying rather than printing a count of nobody.
    /// </summary>
    [Fact]
    public void An_even_split_naming_nobody_is_the_whole_group()
    {
        Assert.Equal("Evenly, between everyone in the group", new EvenSplitRuleDto().Summary(Names));
        Assert.Empty(new EvenSplitRuleDto().Weights(Names));
    }

    [Fact]
    public void An_even_split_naming_people_lists_them()
    {
        var even = new EvenSplitRuleDto([Ana, Lu, Marta]);

        Assert.Equal("Evenly, between Ana, Lu and Marta", even.Summary(Names));
    }

    [Fact]
    public void A_payer_rule_names_nobody_because_who_owes_depends_on_who_paid()
    {
        Assert.Equal("All on whoever paid", new PayerSplitRuleDto().Summary(Names));
        Assert.Empty(new PayerSplitRuleDto().Weights(Names));
    }

    /// <summary>
    /// Without the membership in hand the phrase degrades to a count rather than a column of
    /// raw ids.
    /// </summary>
    [Fact]
    public void Unnamed_people_are_counted_rather_than_printed_as_ids()
    {
        var shares = new SharesSplitRuleDto { Shares = { [Ana] = 2, [Lu] = 1 } };

        Assert.Equal("By shares, between 2", shares.Summary());
    }

    /// <summary>
    /// Somebody the rule still names who has left the group. The server takes a departing
    /// member out of the rules that name them, so this is a form or a superseded version
    /// read against a membership that has moved since -- and it says so rather than printing
    /// a bare id.
    /// </summary>
    [Fact]
    public void Somebody_the_membership_no_longer_holds_is_said_to_have_left()
    {
        var shares = new SharesSplitRuleDto { Shares = { [Ana] = 2, [Guid.NewGuid()] = 1 } };

        Assert.Contains("someone who has left", shares.Summary(Names), StringComparison.Ordinal);
    }

    /// <summary>The shape alone, for a row too narrow for the phrase.</summary>
    [Fact]
    public void The_ratio_is_the_weights_and_nothing_else()
    {
        Assert.Equal("2 : 2 : 1",
            new SharesSplitRuleDto { Shares = { [Ana] = 2, [Lu] = 2, [Marta] = 1 } }.Ratio());

        Assert.Equal("60% : 40%",
            new PercentSplitRuleDto { Percentages = { [Ana] = 60m, [Lu] = 40m } }.Ratio());

        Assert.Equal("evenly", new EvenSplitRuleDto().Ratio());
        Assert.Equal("whoever paid", new PayerSplitRuleDto().Ratio());
    }

    /// <summary>
    /// A category that names no rule divides evenly, and so does a rule whose type nobody has
    /// chosen yet. Neither is broken, and both divide the same way.
    /// </summary>
    [Fact]
    public void Nothing_at_all_divides_evenly()
    {
        SplitRuleDto? nothing = null;

        Assert.Equal("Evenly, between everyone in the group", nothing.Summary(Names));
        Assert.Equal("evenly", nothing.Ratio());
        Assert.Empty(nothing.Weights(Names));
    }
}

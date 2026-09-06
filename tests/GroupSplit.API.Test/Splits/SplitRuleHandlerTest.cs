using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services.SplitRuleHandlers;
using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.API.Test.Splits;

/// <summary>
/// A rule is data; what it does is its handler's. Everything here goes through the
/// dispatcher holding a base-typed <see cref="SplitRule"/>, because that is the only way
/// callers will ever hold one -- if dispatch works anywhere else and not there, it does not
/// work.
/// </summary>
public class SplitRuleHandlerTest
{
    private static readonly Guid Alice = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carol = new("33333333-3333-3333-3333-333333333333");

    private static readonly Guid[] Members = [Alice, Bob, Carol];

    private static readonly ISplitRuleHandler Handler =
        new ServiceCollection().AddSplitRuleServices().BuildServiceProvider()
            .GetRequiredService<ISplitRuleHandler>();

    private static decimal AmountFor(IReadOnlyList<SplitAmount> splits, Guid userId) =>
        splits.SingleOrDefault(split => split.UserId == userId).Amount;

    private static T Naming<T>(T rule, params (Guid UserId, int Weight)[] participants)
        where T : WeightedSplitRule
    {
        foreach (var (userId, weight) in participants)
            rule.Participants.Add(new SplitRuleParticipant { UserId = userId, Weight = weight });

        return rule;
    }

    [Fact]
    public void An_even_rule_naming_nobody_divides_between_the_current_members()
    {
        SplitRule rule = new EvenSplitRule { Name = "Even" };

        var splits = Handler.Divide(rule, 100.00m, Alice, Members);

        Assert.Equal(3, splits.Count);
        Assert.Equal(33.34m, AmountFor(splits, Alice));
        Assert.Equal(100.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// Why an even rule stores no participants by default: it has to keep dividing evenly
    /// when somebody joins, rather than freezing today's membership into weights.
    /// </summary>
    [Fact]
    public void An_even_rule_follows_the_membership()
    {
        SplitRule rule = new EvenSplitRule { Name = "Even" };

        var afterOmarJoins = Handler.Divide(rule, 100.00m, Alice, [..Members, Guid.NewGuid()]);

        Assert.Equal(4, afterOmarJoins.Count);
        Assert.Equal(25.00m, AmountFor(afterOmarJoins, Bob));
    }

    [Fact]
    public void An_even_rule_naming_people_divides_between_only_those()
    {
        SplitRule rule = Naming(new EvenSplitRule { Name = "Trip" }, (Alice, 1), (Bob, 1));

        var splits = Handler.Divide(rule, 100.00m, Alice, Members);

        Assert.Equal(2, splits.Count);
        Assert.Equal(0m, AmountFor(splits, Carol));
    }

    /// <summary>
    /// Shares reach the division as shares. The old model turned them into percentages
    /// first, which is the conversion the rounding bug lived inside; 2:1:1 and 50/25/25 are
    /// now the same arithmetic with nothing in between.
    /// </summary>
    [Fact]
    public void Shares_and_the_percentages_they_amount_to_divide_alike()
    {
        SplitRule shares = Naming(new SharesSplitRule { Name = "Shares" }, (Alice, 2), (Bob, 1), (Carol, 1));
        SplitRule percent = Naming(new PercentSplitRule { Name = "Percent" },
            (Alice, 5000), (Bob, 2500), (Carol, 2500));

        var bySplits = Handler.Divide(shares, 220.50m, Bob, Members).OrderBy(split => split.UserId).ToArray();
        var byPercent = Handler.Divide(percent, 220.50m, Bob, Members).OrderBy(split => split.UserId).ToArray();

        Assert.Equal(byPercent, bySplits);
        Assert.Equal(220.50m, bySplits.Sum(split => split.Amount));
    }

    [Fact]
    public void Percentages_that_do_not_add_up_are_refused()
    {
        SplitRule rule = Naming(new PercentSplitRule { Name = "Percent" }, (Alice, 5000), (Bob, 2500));

        Assert.Equal("Percentages must add up to 100%.", Handler.Invalid(rule));
    }

    [Fact]
    public void A_member_named_twice_is_refused()
    {
        SplitRule rule = Naming(new SharesSplitRule { Name = "Shares" }, (Alice, 1), (Alice, 2));

        Assert.Equal("A member may appear in a rule only once.", Handler.Invalid(rule));
    }

    [Fact]
    public void A_coherent_rule_has_nothing_wrong_with_it()
    {
        Assert.Null(Handler.Invalid(Naming(new PercentSplitRule { Name = "P" }, (Alice, 6000), (Bob, 4000))));
        Assert.Null(Handler.Invalid(Naming(new SharesSplitRule { Name = "S" }, (Alice, 2), (Bob, 1))));
        Assert.Null(Handler.Invalid(new EvenSplitRule { Name = "E" }));
    }

    /// <summary>
    /// The case the participant-weight shape could not express, and the reason nothing about
    /// weights is declared on <see cref="SplitRule"/>: a fixed amount does not scale with
    /// the bill, so no weight stands for it.
    /// </summary>
    /// <remarks>
    /// Defined here rather than shipped. The point is what adding a kind costs: an entity
    /// with its own data, a handler, and a registration -- and not one line changed in the
    /// base, the dispatcher, or any existing handler.
    /// </remarks>
    private sealed class FixedThenEvenSplitRule : SplitRule
    {
        public required Guid FixedUserId { get; init; }

        public required decimal FixedAmount { get; init; }
    }

    private sealed class FixedThenEvenSplitRuleHandler : ISplitRuleHandler<FixedThenEvenSplitRule>
    {
        public IReadOnlyList<SplitAmount> Divide(
            FixedThenEvenSplitRule rule, decimal amount, Guid payerId, IReadOnlyCollection<Guid> members)
        {
            var rest = members.Where(member => member != rule.FixedUserId).ToArray();

            var evenly = SplitCalculator.Divide(amount - rule.FixedAmount, payerId,
                [..rest.Select(member => new SplitWeight(member, 1))]);

            return [new SplitAmount(rule.FixedUserId, rule.FixedAmount), ..evenly];
        }

        public string? Invalid(FixedThenEvenSplitRule rule) =>
            rule.FixedAmount < 0 ? "A fixed amount cannot be negative." : null;

        public SplitRuleDto ToDto(FixedThenEvenSplitRule rule) => new EvenSplitRuleDto();
    }

    [Fact]
    public void A_rule_that_is_not_proportional_needs_no_change_to_anything_existing()
    {
        var provider = new ServiceCollection()
            .AddSplitRuleServices()
            .AddSingleton<ISplitRuleHandler<FixedThenEvenSplitRule>, FixedThenEvenSplitRuleHandler>()
            .BuildServiceProvider();

        SplitRule rule = new FixedThenEvenSplitRule
        {
            Name = "Carol pays ten",
            FixedUserId = Carol,
            FixedAmount = 10.00m
        };

        var splits = provider.GetRequiredService<ISplitRuleHandler>().Divide(rule, 100.00m, Alice, Members);

        Assert.Equal(10.00m, AmountFor(splits, Carol));
        Assert.Equal(45.00m, AmountFor(splits, Bob));
        Assert.Equal(100.00m, splits.Sum(split => split.Amount));
    }

    /// <summary>
    /// A rule nobody registered a handler for fails where it is asked, by name, rather than
    /// falling through to a default that would divide it wrongly and say nothing.
    /// </summary>
    [Fact]
    public void A_rule_with_no_handler_says_so()
    {
        SplitRule rule = new FixedThenEvenSplitRule
        {
            Name = "Unregistered", FixedUserId = Carol, FixedAmount = 1m
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => Handler.Divide(rule, 10m, Alice, Members));

        Assert.Contains(nameof(FixedThenEvenSplitRule), exception.Message);
    }
}

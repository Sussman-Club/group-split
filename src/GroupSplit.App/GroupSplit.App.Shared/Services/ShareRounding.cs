using GroupSplit.App.Shared.Extensions;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// The arithmetic the API checks a division with, done here the same way, so a form cannot
/// call a division complete that the API is about to refuse (#323).
/// </summary>
/// <remarks>
/// Both forms used to check the total they could see. The rule editor rounded the
/// <em>sum</em> of the percentages, so 33.333 / 33.333 / 33.334 showed 100% in green while
/// the API, which stores each percentage in whole hundredths before adding them up, counted
/// 99.99% and refused it. The share fields show two places and added up whatever they held,
/// so three shares of 33.333 read "every cent assigned" under figures that were refused for
/// having a third decimal. Each part is rounded the way it will be stored first, and only
/// then added.
/// </remarks>
public static class ShareRounding
{
    /// <summary>
    /// What a set of percentages totals once each one is stored: rounded to hundredths, half
    /// away from zero, one at a time, and then added -- <c>PercentSplitRuleHandler.FromDto</c>
    /// on the API. The API accepts the rule only when this is exactly 100.
    /// </summary>
    public static decimal PercentTotal(IEnumerable<decimal> percentages) =>
        percentages.Sum(percentage => decimal.Round(percentage, 2, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Whether an amount is whole cents, which is all a share can be: the API refuses a
    /// third decimal place on one rather than rounding it (<c>SplitInput.Amount</c>).
    /// </summary>
    public static bool IsWholeCents(decimal amount) => decimal.Round(amount, 2) == amount;

    /// <summary>
    /// What is left to assign, counted from the shares as they are shown -- to the cent --
    /// so the line under the fields agrees with the figures in them.
    /// </summary>
    public static decimal Remaining(decimal amount, IEnumerable<SplitInput> shares) =>
        amount - shares.Sum(share => decimal.Round(share.Amount, 2, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Why the API would refuse these shares for this amount, or null when it would take
    /// them -- and null for no shares at all, which is not a division but an instruction to
    /// leave it to a rule.
    /// </summary>
    public static string? Unbalanced(decimal amount, IReadOnlyCollection<SplitInput>? shares)
    {
        if (shares is null)
            return null;

        var remaining = Remaining(amount, shares);

        if (remaining > 0m)
            return $"{remaining.ToMoney()} still to assign.";

        if (remaining < 0m)
            return $"{(-remaining).ToMoney()} more than the expense comes to.";

        return shares.All(share => IsWholeCents(share.Amount))
            ? null
            : "A share can only be whole cents.";
    }
}

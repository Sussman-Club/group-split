using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Who carries what a division could not split cleanly.
/// </summary>
/// <remarks>
/// A percentage split of three ways is 33.33 twice and 33.34 once, and the odd cent has to
/// land on somebody. This is the choice of whom, on its own, so that the rule can be stated,
/// tested and swapped without the editor that asks the question knowing the answer.
/// <para>
/// It decides a pre-fill and not a payment. What is actually charged is divided on the
/// server by <c>SplitCalculator</c>, which gives its remainder to the payer -- and because
/// the payer changes from one expense to the next, that rotates on its own. Nothing here
/// rotates: the answer is saved as a rule and then divides every expense in its category, so
/// a policy that picked differently each time would not spread the cent around, it would
/// choose one member to overpay it every time and choose them unrepeatably.
/// </para>
/// </remarks>
public interface IRemainderPolicy
{
    /// <param name="members">Everyone the division is between. Never empty.</param>
    /// <param name="weight">
    /// Each member's share of it: their shares for a shares rule, and the same number for
    /// everyone when the split is even.
    /// </param>
    /// <returns>The member the leftover belongs to.</returns>
    Guid CarriedBy(IReadOnlyCollection<UserInfo> members, Func<UserInfo, decimal> weight);
}

/// <summary>
/// The largest weight, and the lowest id among equals.
/// </summary>
/// <remarks>
/// The rule the server already states: <c>SplitCalculator.RemainderIndex</c> falls back to
/// exactly this once the payer is out of the picture, and a rule has no payer -- it says how
/// to divide, and who paid is not known until an expense uses it. Restated rather than
/// shared because the API is referenced for its OpenAPI document and not its assembly, so
/// there is nothing to call.
/// <para>
/// Largest first has a reason beyond agreeing with the server: the adjustment is then the
/// smallest part of the number it moves, and it can never land on a member holding no share
/// at all -- who was left out of the split, and would be put back into it by a hundredth of
/// a percent.
/// </para>
/// </remarks>
public sealed class LargestShareRemainderPolicy : IRemainderPolicy
{
    public Guid CarriedBy(IReadOnlyCollection<UserInfo> members, Func<UserInfo, decimal> weight) =>
        members
            .OrderByDescending(weight)
            .ThenBy(member => member.Id)
            .First()
            .Id;
}

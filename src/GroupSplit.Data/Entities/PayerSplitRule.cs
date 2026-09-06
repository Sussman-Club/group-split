namespace GroupSplit.Data.Entities;

/// <summary>
/// Not shared: whoever paid owes all of it.
/// </summary>
/// <remarks>
/// What the personal rule used to mean, and the first rule in the model that is not
/// proportional. It names nobody and weighs nothing -- who owes depends on who paid, which
/// is known when the expense is written and not before, so no list of participants could
/// express it.
/// <para>
/// It extends <see cref="SplitRule"/> directly rather than
/// <see cref="WeightedSplitRule"/>, which is the whole reason the base declares nothing
/// about weights: a kind that does not divide in proportion needs to change nothing to
/// exist.
/// </para>
/// </remarks>
public sealed class PayerSplitRule : SplitRule;

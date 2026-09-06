namespace GroupSplit.Data.Entities;

/// <summary>
/// In proportion to stated percentages, held as hundredths of a percent -- 33.33% is 3333,
/// and a complete rule's weights sum to 10000.
/// </summary>
/// <remarks>
/// A whole number because a percentage of a bill is not a measurement. The old model kept
/// these as a <c>double</c>, which is how 33.33 came to be 33.329999999999998 and why the
/// arithmetic needed an epsilon to decide whether a rule added up.
/// </remarks>
public sealed class PercentSplitRule : WeightedSplitRule;

namespace GroupSplit.Data.Entities;

/// <summary>
/// In proportion to whole shares -- "Anabel counts for two".
/// </summary>
/// <remarks>
/// Shares reach the division untouched. The old model converted them to percentages first,
/// rounding each to two decimal places and then patching the drift onto whichever
/// participant a dictionary enumerated last -- a conversion that existed only because the
/// balance query could read percentages and nothing else. Weights are proportional and the
/// division normalises by their total, so 2:1:1 divides exactly as well as 50/25/25 and
/// there is nothing to convert or to round twice.
/// </remarks>
public sealed class SharesSplitRule : WeightedSplitRule;

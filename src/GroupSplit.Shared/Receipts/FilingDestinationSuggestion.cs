namespace GroupSplit.Shared;

/// <summary>A suggested destination for spending, regardless of how it was entered.</summary>
/// <remarks>
/// Either a category, or -- when none of the group's categories fits but one of its split
/// rules does -- a rule and no category. Exactly one of <paramref name="CategoryId"/> and
/// <paramref name="SplitRuleId"/> is set.
/// </remarks>
public sealed record FilingDestinationSuggestion(
    Guid GroupId,
    string GroupName,
    bool IsPersonal,
    Guid? CategoryId,
    string? CategoryName,
    double Confidence,
    Guid? SplitRuleId = null,
    string? SplitRuleName = null)
{
    public string Destination => $"{GroupName} · {CategoryName ?? SplitRuleName}";
}

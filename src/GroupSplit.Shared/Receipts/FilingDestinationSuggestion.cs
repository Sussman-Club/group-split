namespace GroupSplit.Shared;

/// <summary>A suggested destination for spending, regardless of how it was entered.</summary>
/// <remarks>
/// Either a category, or -- when none of the group's categories fits but one of its split
/// rules does -- a rule and no category. At most one of <paramref name="CategoryId"/> and
/// <paramref name="SplitRuleId"/> is set; a merchant's last expense can name neither, and
/// then only the group is suggested.
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
    public string Destination => (CategoryName ?? SplitRuleName) is { } what ? $"{GroupName} · {what}" : GroupName;
}

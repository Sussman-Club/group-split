namespace GroupSplit.Shared;

/// <summary>A suggested destination for spending, regardless of how it was entered.</summary>
public sealed record FilingDestinationSuggestion(
    Guid GroupId,
    string GroupName,
    bool IsPersonal,
    Guid CategoryId,
    string CategoryName,
    double Confidence)
{
    public string Destination => $"{GroupName} · {CategoryName}";
}

namespace GroupSplit.Shared;

public record RuleDetailsResponse
{
    public Guid RuleId { get; set; }
    public Guid RuleVersionId { get; set; }
    public string Category { get; set; } = null!;
    public RuleVersionDto Version { get; set; } = null!;
}
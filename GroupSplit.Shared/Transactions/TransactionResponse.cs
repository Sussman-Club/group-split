namespace GroupSplit.Shared;

public record TransactionResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset DateTime { get; set; }
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public Guid PaidByUserId { get; set; }
    public string PaidByUserName { get; set; } = "";
    public Guid RuleVersionId { get; set; }
    public string Category { get; set; } = "";
}
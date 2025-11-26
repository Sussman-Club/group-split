namespace GroupSplit.Shared;

public record CreateTransactionRequest
{
    public Guid? PaidByUserId { get; set; }
    public Guid? RuleVersionId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset Date { get; set; }
};
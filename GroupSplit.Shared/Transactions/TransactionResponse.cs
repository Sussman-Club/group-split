namespace GroupSplit.Shared;

public record TransactionResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset Date { get; set; }
    public string GroupName { get; set; } = "";
    public string PaidByName { get; set; } = "";
    public string Category { get; set; } = "";
}
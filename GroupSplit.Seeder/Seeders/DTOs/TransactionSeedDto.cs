namespace GroupSplit.Seeder.Seeders.DTOs;

public class TransactionSeedDto
{
    public required Guid Id { get; init; }
    public required Guid PayerId { get; init; }
    public required Guid RuleVersionId { get; init; }
    public required decimal Amount { get; set; }
    public required DateTimeOffset DateTime { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}
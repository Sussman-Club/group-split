namespace GroupSplit.Seeder.Seeders.DTOs;

public class TransactionSeedDto
{
    public required Guid Id { get; init; }
    public required Guid PayerId { get; init; }
    /// <summary>
    /// What it was filed under, or null for a personal expense -- one the payer recorded
    /// for themselves, in no group and under no category. A category belongs to a group,
    /// so naming one is also what says which group the expense is in.
    /// </summary>
    public Guid? CategoryId { get; init; }
    public required decimal Amount { get; set; }
    public required DateTimeOffset DateTime { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}
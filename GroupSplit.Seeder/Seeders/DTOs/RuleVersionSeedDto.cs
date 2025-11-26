namespace GroupSplit.Seeder.Seeders.DTOs;

public class RuleVersionSeedDto
{
    public required Guid Id { get; init; }
    public required Guid RuleId { get; init; }
    public required DateOnly StartDate { get; init; }
}
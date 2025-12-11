using GroupSplit.Shared;

namespace GroupSplit.Seeder.Seeders.DTOs;

public class RuleSeedDto
{
    public required Guid Id { get; init; }
    public required Guid GroupId { get; init; }
    public required string Category { get; init; }
    public required RuleVersionDto SplitRule { get; init; }
}
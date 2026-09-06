using GroupSplit.Shared;

namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// A seeded category, and the division it defaults to.
/// </summary>
/// <remarks>
/// One seed file still describes both, because that is how a group thinks of them --
/// "Groceries is split four ways" -- even though they are two rows now. <see cref="Id"/> is
/// the category's, so a transaction that named this in the old seed data still names it.
/// </remarks>
public class CategorySeedDto
{
    public required Guid Id { get; init; }
    public required Guid GroupId { get; init; }
    public required string Category { get; init; }
    public required SplitRuleDto SplitRule { get; init; }
}

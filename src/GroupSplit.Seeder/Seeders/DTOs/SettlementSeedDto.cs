namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// One repayment between two members of a seeded group.
/// </summary>
/// <remarks>
/// Carries no id, unlike every other seed type. A transfer is only ever built through
/// <c>Transfer.Between</c> -- the factory is what guarantees the single split that makes
/// the balances come out right -- and that factory does not take one. Rather than widen a
/// domain type for a seeder's convenience, the seeder matches on what actually identifies
/// a repayment: who paid whom, how much, in which group, when.
/// </remarks>
public class SettlementSeedDto
{
    public required Guid GroupId { get; init; }

    /// <summary>Who handed the money over.</summary>
    public required Guid FromUserId { get; init; }

    /// <summary>Who received it.</summary>
    public required Guid ToUserId { get; init; }

    public required decimal Amount { get; init; }

    public required DateTimeOffset DateTime { get; init; }

    /// <summary>What was remembered about it -- "cash", "end of September".</summary>
    public string? Description { get; init; }
}

namespace GroupSplit.Seeder.Seeders.DTOs;

/// <summary>
/// A linked bank and everything it has sent, as demo data.
/// </summary>
/// <remarks>
/// One entry describes the whole chain -- the institution, its accounts, and the rows
/// waiting in the inbox -- because that is how somebody thinks of a linked bank, and
/// because the three tables are useless to a demo one at a time.
/// <para>
/// The provider is deliberately not <c>plaid</c>. Nothing can sync these: there is no
/// connector for the seeded provider and the stored token is not a real one. That is the
/// point -- the inbox has something in it for anyone reviewing the app or the pull request,
/// without a Plaid account, and no sweep will ever try to refresh it.
/// </para>
/// </remarks>
public class BankConnectionSeedDto
{
    public required Guid Id { get; init; }

    public required Guid UserId { get; init; }

    public required string InstitutionName { get; init; }

    public required List<LinkedAccountSeedDto> Accounts { get; init; }
}

public class LinkedAccountSeedDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Mask { get; init; }

    public required string Type { get; init; }

    public string? Subtype { get; init; }

    public required List<BankTransactionSeedDto> Transactions { get; init; }
}

/// <summary>
/// One row as the bank sent it. Positive is money out, the same convention an expense uses.
/// </summary>
public class BankTransactionSeedDto
{
    public required Guid Id { get; init; }

    /// <summary>Days before the seeding run, so demo data never looks stale.</summary>
    public required int DaysAgo { get; init; }

    public required decimal Amount { get; init; }

    public required string Description { get; init; }

    public string? MerchantName { get; init; }

    public string? ProviderCategory { get; init; }

    public bool Pending { get; init; }
}

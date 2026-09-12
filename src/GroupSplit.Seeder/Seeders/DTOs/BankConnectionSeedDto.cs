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

    /// <summary>
    /// Who was paid. Matched against <c>merchants.json</c> by name to find the shop -- and
    /// with it the logo, which is the merchant's and not the row's. A name that file does
    /// not list still shows on the row; it just has no mark to go with it.
    /// </summary>
    public string? MerchantName { get; init; }

    public string? ProviderCategory { get; init; }

    /// <summary>The provider's finer category, under the primary one.</summary>
    public string? ProviderCategoryDetailed { get; init; }

    /// <summary>
    /// Days before the run that the money was actually spent, when that differs from when
    /// it posted. Null means the two are the same, which is the case for a pending row and
    /// for anything a bank settles the same day.
    /// </summary>
    /// <remarks>
    /// Seeded on purpose for a few rows: a card charge posting days after the event is the
    /// ordinary case, and demo data that never showed it would hide the one date somebody
    /// actually recognises.
    /// </remarks>
    public int? AuthorizedDaysAgo { get; init; }

    /// <summary>How it was paid: <c>in store</c>, <c>online</c>, <c>other</c>.</summary>
    public string? PaymentChannel { get; init; }

    public string? City { get; init; }

    public bool Pending { get; init; }

    /// <summary>
    /// The itemised bill behind this charge, for the rows a demo is meant to file by hand.
    /// Null for the ordinary row, which is every charge nobody kept the paper for.
    /// </summary>
    /// <remarks>
    /// This is the state the feature exists for, and the only one a seeder can reach: the
    /// card is charged at the shop, the row lands in the inbox, and the lines are there
    /// before any expense is. Filing the row whole hands the bill to the expense, so a
    /// category with an itemised rule divides by it on the spot; splitting the row instead
    /// gives each group of lines an expense of its own.
    /// <para>
    /// Its derived total has to equal <see cref="Amount"/>, and the seeder refuses the row
    /// rather than seed a bill that is not this charge.
    /// </para>
    /// </remarks>
    public ReceiptSeedDto? Receipt { get; init; }
}

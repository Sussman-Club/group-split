using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Seeds a linked bank per demo account, with transactions waiting in the inbox.
/// </summary>
/// <remarks>
/// Without this the inbox is empty for everyone who has not linked a real bank, which means
/// nobody can see the feature -- or review it -- without a Plaid account of their own.
/// <para>
/// Nothing here can be synced, on purpose. The provider is <see cref="Provider"/> and no
/// connector answers for it, so the nightly sweep passes over these and the stored token is
/// never read. It is a placeholder rather than a token, because a real one does not belong
/// in a file in the repository.
/// </para>
/// </remarks>
[DependsOn(typeof(UserSeeder))]
[DependsOn(typeof(MerchantSeeder))]
public class BankConnectionSeeder(
    AppDbContext db,
    TimeProvider clock,
    ILogger<BankConnectionSeeder> logger,
    ISeedDataSource<BankConnectionSeedDto> source)
    : AppDbContextSeeder<BankConnection, BankConnectionSeedDto>(db, source, logger)
{
    /// <summary>
    /// Not <c>plaid</c>: no connector answers for this, which is what keeps every sync away
    /// from data that has no bank behind it.
    /// </summary>
    private const string Provider = "seed";

    /// <summary>
    /// The merchants this run has already looked up, by the key the unique index is on. The
    /// seed file names the same shops across several demo accounts and several rows each.
    /// </summary>
    private readonly Dictionary<string, Merchant?> _merchants = [];

    protected override async Task<BankConnection?> MapAsync(BankConnectionSeedDto dto, CancellationToken ct = default)
    {
        var user = await DbContext.Set<User>().FindAsync([dto.UserId], ct);

        if (user is null)
            return null;

        var now = clock.GetUtcNow();

        var connection = new BankConnection
        {
            Id = dto.Id,
            User = user,
            Provider = Provider,
            ProviderItemId = $"seed-{dto.Id:N}",
            InstitutionName = dto.InstitutionName,
            // Never read: nothing syncs a seeded connection. A real token in a repository
            // would be a real problem, and a fake one that looked real would invite someone
            // to try.
            AccessTokenCiphertext = "seeded-connection-has-no-token",
            LinkedAt = now.AddDays(-30),
            LastSyncedAt = now.AddHours(-1)
        };

        foreach (var accountDto in dto.Accounts)
        {
            var account = new LinkedAccount
            {
                Id = accountDto.Id,
                ProviderAccountId = $"seed-{accountDto.Id:N}",
                Name = accountDto.Name,
                Mask = accountDto.Mask,
                Type = accountDto.Type,
                Subtype = accountDto.Subtype
            };

            foreach (var rowDto in accountDto.Transactions)
            {
                account.Transactions.Add(new BankTransaction
                {
                    Id = rowDto.Id,
                    ProviderTransactionId = $"seed-{rowDto.Id:N}",
                    // Relative to the run, so a database seeded months ago still reads as
                    // something that arrived this week.
                    Date = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-rowDto.DaysAgo)),
                    Amount = rowDto.Amount,
                    Description = rowDto.Description,
                    MerchantName = rowDto.MerchantName,
                    ProviderCategory = rowDto.ProviderCategory,
                    ProviderCategoryDetailed = rowDto.ProviderCategoryDetailed,
                    AuthorizedDate = rowDto.AuthorizedDaysAgo is { } spent
                        ? DateOnly.FromDateTime(now.UtcDateTime.AddDays(-spent))
                        : null,
                    PaymentChannel = rowDto.PaymentChannel,
                    City = rowDto.City,
                    Merchant = Merchant(rowDto),
                    Pending = rowDto.Pending,
                    RawJson = "{}",
                    ImportedAt = now.AddHours(-1)
                });
            }

            connection.Accounts.Add(account);
        }

        return connection;
    }

    /// <summary>
    /// Adds the connection and, alongside it, the bills for the rows that have one.
    /// </summary>
    /// <remarks>
    /// Here rather than in <c>MapAsync</c>, and that is the whole reason this override
    /// exists: a mapped connection that turns out to be seeded already is dropped without
    /// ever being added, so a bill added while mapping would be a bill added on every run.
    /// <para>
    /// A receipt is not reachable through the connection's own graph -- a bank row does not
    /// navigate to its bill, on purpose, since the row is a fact from the bank and the bill
    /// is something a person typed. So it is added on its own with the row's id on it.
    /// </para>
    /// </remarks>
    protected override Task AddEntityAsync(
        BankConnection entity, BankConnectionSeedDto dto, CancellationToken ct = default)
    {
        var bills = dto.Accounts
            .SelectMany(account => account.Transactions)
            .Where(row => row.Receipt is not null)
            .Select(row => Bill(row));

        DbContext.Set<Receipt>().AddRange(bills);

        return base.AddEntityAsync(entity, dto, ct);
    }

    /// <summary>
    /// The bill on a seeded row, checked against the charge it claims to be.
    /// </summary>
    /// <remarks>
    /// Thrown rather than logged, because there is nothing useful to seed in its place. A
    /// bill whose lines do not come to the charge cannot be filed and cannot be split --
    /// both refuse it by name -- so a demo that carried on would leave a row that looks
    /// ready and fails the moment anybody touches it, which is worse than a seed run that
    /// stops and says which row it was.
    /// </remarks>
    private static Receipt Bill(BankTransactionSeedDto row)
    {
        // Null-forgiving: the caller filtered on it, and a nullable parameter here would only
        // move the same fact somewhere it reads as an open question.
        var receipt = SeededBill.From(row.Receipt!, expenseId: null);

        if (receipt.Total != row.Amount)
        {
            throw new InvalidOperationException(
                $"Seeded bank row {row.Id} ({row.Description}) is {row.Amount:0.00}, but its " +
                $"bill comes to {receipt.Total:0.00}.");
        }

        receipt.BankTransactionId = row.Id;

        return receipt;
    }

    /// <summary>
    /// The seeded shop this row names, or null for one <c>merchants.json</c> does not list.
    /// Looked up and never created: <see cref="MerchantSeeder"/> owns that table, because
    /// this seeder and the expenses' one run at the same time and both point at it.
    /// </summary>
    private Merchant? Merchant(BankTransactionSeedDto row)
    {
        if (string.IsNullOrWhiteSpace(row.MerchantName))
            return null;

        var normalized = row.MerchantName.Trim().ToLowerInvariant();

        if (_merchants.TryGetValue(normalized, out var seen))
            return seen;

        var merchant = DbContext.Set<Merchant>()
            .FirstOrDefault(candidate => candidate.NormalizedName == normalized);

        if (merchant is null)
        {
            logger.LogInformation(
                "Seeded bank row names {Merchant}, which merchants.json does not list; it will show as initials.",
                row.MerchantName);
        }

        _merchants[normalized] = merchant;

        return merchant;
    }
}

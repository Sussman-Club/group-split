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
                    LogoUrl = rowDto.LogoUrl,
                    Pending = rowDto.Pending,
                    RawJson = "{}",
                    ImportedAt = now.AddHours(-1)
                });
            }

            connection.Accounts.Add(account);
        }

        return connection;
    }
}

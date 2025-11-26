using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(RuleVersionSeeder))]
[DependsOn(typeof(UserSeeder))]
public class TransactionSeeder(
    AppDbContext db,
    ILogger<TransactionSeeder> logger,
    ISeedDataSource<TransactionSeedDto> source)
    : AppDbContextSeeder<Transaction, TransactionSeedDto>(db, source, logger)
{
    protected override async Task<Transaction?> MapAsync(TransactionSeedDto dto, CancellationToken ct = default)
    {
        var payer = await DbContext.Set<User>().FindAsync([dto.PayerId], ct);

        var ruleVersion = await DbContext.Set<RuleVersion>().FindAsync([dto.RuleVersionId], ct);

        if (payer is null || ruleVersion is null)
            return null;

        return new Transaction
        {
            Id = dto.Id,
            Amount = dto.Amount,
            Name = dto.Name,
            Description = dto.Description,
            DateTime = dto.DateTime,
            User = payer,
            RuleVersion = ruleVersion,
        };
    }
}
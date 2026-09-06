using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Splitting;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(RuleSeeder))]
[DependsOn(typeof(UserSeeder))]
public class TransactionSeeder(
    AppDbContext db,
    ILogger<TransactionSeeder> logger,
    ISeedDataSource<TransactionSeedDto> source)
    : AppDbContextSeeder<Expense, TransactionSeedDto>(db, source, logger)
{
    protected override async Task<Expense?> MapAsync(TransactionSeedDto dto, CancellationToken ct = default)
    {
        var payer = await DbContext.Set<User>().FindAsync([dto.PayerId], ct);

        var ruleVersion = await DbContext.Set<RuleVersion>()
            .Include(version => version.Rule)
            .ThenInclude(rule => rule.Group)
            .FirstOrDefaultAsync(x => x.Rule.Id == dto.RuleId, ct);

        if (payer is null || ruleVersion is null)
            return null;

        var expense = new Expense
        {
            Id = dto.Id,
            Amount = dto.Amount,
            Currency = ruleVersion.Rule.Group.Currency,
            Name = dto.Name,
            Description = dto.Description,
            DateTime = dto.DateTime,
            User = payer,
            Group = ruleVersion.Rule.Group,
            RuleVersion = ruleVersion,
        };

        // Through the same division the app uses, so a developer's seeded balances are
        // ones the app could actually have produced.
        await DbContext.WriteSplitsAsync(expense, ct);

        return expense;
    }
}
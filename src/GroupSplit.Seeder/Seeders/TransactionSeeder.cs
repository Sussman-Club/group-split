using GroupSplit.Data;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

[DependsOn(typeof(CategorySeeder))]
[DependsOn(typeof(UserSeeder))]
public class TransactionSeeder(
    AppDbContext db,
    ILogger<TransactionSeeder> logger,
    IExpenseSplitter splitter,
    ISeedDataSource<TransactionSeedDto> source)
    : AppDbContextSeeder<Expense, TransactionSeedDto>(db, source, logger)
{
    protected override async Task<Expense?> MapAsync(TransactionSeedDto dto, CancellationToken ct = default)
    {
        var payer = await DbContext.Set<User>().FindAsync([dto.PayerId], ct);

        var category = await DbContext.Set<Category>()
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.Id == dto.CategoryId, ct);

        if (payer is null || category is null)
            return null;

        var expense = new Expense
        {
            Id = dto.Id,
            Amount = dto.Amount,
            Currency = category.Group.Currency,
            Name = dto.Name,
            Description = dto.Description,
            DateTime = dto.DateTime,
            User = payer,
            Group = category.Group,
            Category = category,
        };

        // Through the same division the app uses, so a developer's seeded balances are
        // ones the app could actually have produced.
        await splitter.WriteSplitsAsync(expense, ct);

        return expense;
    }
}

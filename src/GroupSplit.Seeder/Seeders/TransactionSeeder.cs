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
[DependsOn(typeof(MerchantSeeder))]
public class TransactionSeeder(
    AppDbContext db,
    ILogger<TransactionSeeder> logger,
    IExpenseSplitter splitter,
    TimeProvider clock,
    ISeedDataSource<TransactionSeedDto> source)
    : AppDbContextSeeder<Expense, TransactionSeedDto>(db, source, logger)
{
    /// <summary>
    /// The day an expense is recorded against: a fixed one for the history, or a relative
    /// one for the few that have to sit beside a seeded bank row.
    /// </summary>
    /// <remarks>
    /// Given a time of day rather than midnight either way, because a person records an
    /// expense at a moment and the ledger sorts on it.
    /// </remarks>
    protected DateTimeOffset When(TransactionSeedDto dto) =>
        dto.DaysAgo is { } days
            ? new DateTimeOffset(
                clock.GetUtcNow().UtcDateTime.Date.AddDays(-days).Add(new TimeSpan(20, 30, 0)), TimeSpan.Zero)
            : dto.DateTime ?? throw new InvalidOperationException(
                $"Seeded expense {dto.Id} gives neither a date nor a number of days ago.");

    protected override async Task<Expense?> MapAsync(TransactionSeedDto dto, CancellationToken ct = default)
    {
        var payer = await DbContext.Set<User>().FindAsync([dto.PayerId], ct);

        if (payer is null)
            return null;

        // No category means a personal expense: no group either, since a category is what
        // says which group an expense is in. It divides to a single share, the payer's own.
        var category = dto.CategoryId is { } categoryId
            ? await DbContext.Set<Category>()
                .Include(c => c.Group)
                .FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            : null;

        if (dto.CategoryId is not null && category is null)
            return null;

        var expense = new Expense
        {
            Id = dto.Id,
            Amount = dto.Amount,
            Currency = category?.Group.Currency ?? Currencies.Default,
            Name = dto.Name,
            Description = dto.Description,
            DateTime = When(dto),
            User = payer,
            Group = category?.Group,
            Category = category,
            // Where it was spent, for the seeded expenses that name a shop. A real one gets
            // this from the bank row it was filed from; nothing files a seeded expense, so
            // without this every list in the demo shows initials and nothing else.
            Merchant = await MerchantAsync(dto.Merchant, ct)
        };

        // Through the same division the app uses, so a developer's seeded balances are
        // ones the app could actually have produced.
        await splitter.WriteSplitsAsync(expense, ct);

        return expense;
    }

    /// <summary>
    /// The seeded shop of that name, or null for an expense that names none. Looked up and
    /// never created -- <see cref="MerchantSeeder"/> owns that table.
    /// </summary>
    /// <remarks>
    /// Cached per run because the seed file names the same handful of shops across hundreds
    /// of expenses, and a query each would be hundreds of round trips for fourteen answers.
    /// </remarks>
    private async Task<Merchant?> MerchantAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = name.Trim().ToLowerInvariant();

        if (_merchants.TryGetValue(normalized, out var seen))
            return seen;

        var merchant = await DbContext.Set<Merchant>()
            .FirstOrDefaultAsync(candidate => candidate.NormalizedName == normalized, ct);

        if (merchant is null)
        {
            logger.LogInformation(
                "Seeded expense names {Merchant}, which merchants.json does not list; it will show as initials.",
                name);
        }

        _merchants[normalized] = merchant;

        return merchant;
    }

    private readonly Dictionary<string, Merchant?> _merchants = [];
}

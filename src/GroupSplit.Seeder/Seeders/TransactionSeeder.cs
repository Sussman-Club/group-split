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
// And after the invitations, because a group divides its spending between its participants
// -- its members and the people it has invited and is waiting on. Seeded the other way
// round, every balance here would be computed as though nobody had been invited.
[DependsOn(typeof(GroupInvitationSeeder))]
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

        // Before the division, and in memory rather than saved first: an itemised rule reads
        // the bill off the expense, and ExpenseSplitter only goes looking in the table for
        // one the navigation does not already carry. Attached here, a seeded expense divides
        // by its own bill on the first pass with nothing written yet.
        if (dto.Receipt is { } bill)
            expense.Receipt = Bill(bill, expense);

        // Through the same division the app uses, so a developer's seeded balances are
        // ones the app could actually have produced.
        await splitter.WriteSplitsAsync(expense, ct);

        return expense;
    }

    /// <summary>
    /// The seeded bill, with its figures worked out from its lines.
    /// </summary>
    /// <remarks>
    /// The subtotal is the lines added up and the total is that plus the extras, so the seed
    /// file cannot state a receipt that disagrees with itself. What it can still state is one
    /// that disagrees with the <em>expense</em>, and that is left to fail: the division
    /// refuses a bill whose total is not the expense's amount, by name and with both figures,
    /// which is a better thing for a seed run to say than a balance nobody checked.
    /// </remarks>
    private static Receipt Bill(ReceiptSeedDto dto, Expense expense)
    {
        var subtotal = dto.Items.Sum(line => line.Price);

        var receipt = new Receipt
        {
            ExpenseId = expense.Id,
            Subtotal = subtotal,
            Tax = dto.Tax,
            Tip = dto.Tip,
            Total = subtotal + dto.Tax + dto.Tip
        };

        foreach (var line in dto.Items)
        {
            var item = new ReceiptItem
            {
                Name = line.Name,
                TotalPrice = line.Price,
                Quantity = line.Quantity,
                UnitPrice = line.Quantity == 0
                    ? line.Price
                    : decimal.Round(line.Price / line.Quantity, 2),
                Division = line.Shared
                    ? ReceiptItemDivision.Evenly
                    : ReceiptItemDivision.Claimed
            };

            foreach (var (userId, weight) in line.Had)
                item.Claims.Add(new ReceiptItemClaim { UserId = userId, Weight = weight });

            receipt.Items.Add(item);
        }

        return receipt;
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

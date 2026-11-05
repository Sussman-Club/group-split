using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

// A private receipt matching an existing demo expense makes the inbox's attach action visible.
[DependsOn(typeof(TransactionSeeder))]
public sealed class ReceiptInboxSeeder(AppDbContext db) : ISeeder
{
    private static readonly Guid ReceiptId = Guid.Parse("e6a8651b-1c59-4ef0-b57f-0aa1e0ecaf01");
    private static readonly Guid ExpenseId = Guid.Parse("d0000005-0000-4000-8000-000000000005");

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var expense = await db.Set<Expense>().SingleOrDefaultAsync(e => e.Id == ExpenseId, ct);
        if (expense is null) return;

        if (!await db.Set<Receipt>().AnyAsync(r => r.Id == ReceiptId, ct))
        {
            var receipt = new Receipt
            {
                Id = ReceiptId, OwnerUserId = expense.UserId, Currency = expense.Currency,
                MerchantName = "Pharmacy 88", PurchaseDate = DateOnly.FromDateTime(expense.DateTime.UtcDateTime),
                Subtotal = expense.Amount, Total = expense.Amount
            };
            receipt.Items.Add(new ReceiptItem { Name = "Painkillers", NormalizedName = "painkillers",
                UnitPrice = expense.Amount, TotalPrice = expense.Amount });
            db.Add(receipt);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var examples = new[]
        {
            ("e6a8651b-1c59-4ef0-b57f-0aa1e0ecaf02", "Lidl", "Groceries", 48.75m, 1, false),
            ("e6a8651b-1c59-4ef0-b57f-0aa1e0ecaf03", "IKEA", "Kitchen storage", 29.90m, 2, false),
            ("e6a8651b-1c59-4ef0-b57f-0aa1e0ecaf04", "Cafe Central", "Lunch", 16.40m, 4, false),
            ("e6a8651b-1c59-4ef0-b57f-0aa1e0ecaf05", "Bookshop", "Book", 12.50m, 6, true)
        };
        var ids = examples.Select(example => Guid.Parse(example.Item1)).ToArray();
        var existing = await db.Set<Receipt>().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        foreach (var (id, merchant, item, total, daysAgo, ignored) in examples)
        {
            var receiptId = Guid.Parse(id);
            if (existing.Contains(receiptId)) continue;
            var receipt = new Receipt { Id = receiptId, OwnerUserId = expense.UserId, Currency = expense.Currency,
                MerchantName = merchant, PurchaseDate = today.AddDays(-daysAgo), Subtotal = total, Total = total, IsIgnored = ignored };
            receipt.Items.Add(new ReceiptItem { Name = item, NormalizedName = item.ToLowerInvariant(), UnitPrice = total, TotalPrice = total });
            db.Add(receipt);
        }
        await db.SaveChangesAsync(ct);
    }
}

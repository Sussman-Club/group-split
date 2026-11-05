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
        if (await db.Set<Receipt>().AnyAsync(r => r.Id == ReceiptId, ct)) return;
        var expense = await db.Set<Expense>().SingleOrDefaultAsync(e => e.Id == ExpenseId, ct);
        if (expense is null) return;

        var receipt = new Receipt
        {
            Id = ReceiptId, OwnerUserId = expense.UserId, Currency = expense.Currency,
            MerchantName = "Pharmacy 88", PurchaseDate = DateOnly.FromDateTime(expense.DateTime.UtcDateTime),
            Subtotal = expense.Amount, Total = expense.Amount
        };
        receipt.Items.Add(new ReceiptItem { Name = "Painkillers", NormalizedName = "painkillers",
            UnitPrice = expense.Amount, TotalPrice = expense.Amount });
        db.Add(receipt);
        await db.SaveChangesAsync(ct);
    }
}

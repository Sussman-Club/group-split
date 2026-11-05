using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Seeders;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Test.Seeding;

public class ReceiptInboxSeederTest
{
    [Fact]
    public async Task Seeds_one_private_receipt_matching_the_existing_expense()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var expense = new Expense
        {
            Id = Guid.Parse("d0000005-0000-4000-8000-000000000005"),
            UserId = Guid.NewGuid(), GroupId = Guid.NewGuid(), Name = "Painkillers",
            Amount = 9.35m, Currency = "USD", DateTime = DateTimeOffset.Parse("2026-10-31T20:30:00Z")
        };
        db.Add(expense);
        await db.SaveChangesAsync();
        var seeder = new ReceiptInboxSeeder(db);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var receipt = await db.Set<Receipt>().Include(r => r.Items).SingleAsync();
        Assert.Equal(expense.UserId, receipt.OwnerUserId);
        Assert.Null(receipt.ExpenseId);
        Assert.Equal(expense.Amount, receipt.Total);
        Assert.Equal(expense.Currency, receipt.Currency);
        Assert.Equal(DateOnly.FromDateTime(expense.DateTime.UtcDateTime), receipt.PurchaseDate);
        Assert.Single(receipt.Items);
    }
}

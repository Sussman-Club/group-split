using System.Text.Json;
using GroupSplit.Seeder.Seeders.DTOs;
using GroupSplit.Shared;

namespace GroupSplit.Seeder.Test.Seeding;

public class SeededReceiptTest
{
    [Fact]
    public void Seeded_bills_add_up_and_itemized_expenses_name_item_rules()
    {
        var root = FindRoot();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var transactions = JsonSerializer.Deserialize<List<TransactionSeedDto>>(File.ReadAllText(Path.Combine(root, "transactions.json")), options)!;
        var categories = JsonSerializer.Deserialize<List<CategorySeedDto>>(File.ReadAllText(Path.Combine(root, "categories.json")), options)!;
        var bills = transactions.Where(t => t.Receipt is not null).ToList();
        Assert.NotEmpty(bills);
        foreach (var expense in bills)
        {
            var bill = expense.Receipt!;
            Assert.Equal(expense.Amount, bill.Items.Sum(i => i.Price) + bill.Tax + bill.Tip);
            Assert.Equal(bill.Tax, bill.Items.Sum(i => i.TaxAmount));
            if (categories.Single(c => c.Id == expense.CategoryId).SplitRule is ItemizedSplitRuleDto)
                Assert.All(bill.Items, i => { Assert.NotNull(i.SplitRule); Assert.False(string.IsNullOrWhiteSpace(i.RuleName)); Assert.IsNotType<ItemizedSplitRuleDto>(i.SplitRule); });
        }
    }
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../..", "src/GroupSplit.Seeder/SeedData"));
}

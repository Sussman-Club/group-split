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
            if (expense.CategoryId is { } categoryId && categories.Single(c => c.Id == categoryId).SplitRule is ItemizedSplitRuleDto)
            {
                Assert.All(bill.Items, i => { Assert.NotNull(i.SplitRule); Assert.False(string.IsNullOrWhiteSpace(i.RuleName)); Assert.IsNotType<ItemizedSplitRuleDto>(i.SplitRule); });
            }
        }
    }

    [Fact]
    public void Anabel_has_multicurrency_examples_and_an_euro_default_group()
    {
        var root = FindRoot();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var transactions = JsonSerializer.Deserialize<List<TransactionSeedDto>>(File.ReadAllText(Path.Combine(root, "transactions.json")), options)!;
        var groups = JsonSerializer.Deserialize<List<GroupSeedDto>>(File.ReadAllText(Path.Combine(root, "groups.json")), options)!;
        var anabel = Guid.Parse("B2C1F8E1-8B2A-4E77-9A29-8F0E570A4F64");
        var examples = transactions.Where(expense => expense.PayerId == anabel && expense.Currency is not null).ToList();
        Assert.Contains(examples, expense => expense.Currency == "EUR" && expense.Receipt is not null);
        Assert.Contains(examples, expense => expense.Currency == "JPY" && expense.Receipt is not null);
        Assert.Contains(examples, expense => expense.Currency == "KWD" && expense.Amount == 3.125m);
        Assert.Contains(groups, group => group.Id == "45500001-0000-4000-8000-000000000001" && group.Currency == "EUR");
    }
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../..", "src/GroupSplit.Seeder/SeedData"));
}

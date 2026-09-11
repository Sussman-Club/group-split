using System.Text.Json;
using GroupSplit.Seeder.Seeders.DTOs;
using GroupSplit.Shared;

namespace GroupSplit.Seeder.Test.Seeding;

/// <summary>
/// The seed files, read as they ship.
/// </summary>
/// <remarks>
/// An itemised rule is the first kind whose division depends on something other than the
/// rule, so it is the first that a seed file can get wrong in a way nothing catches until
/// the run. Two ways, both of which stop the seeder dead rather than producing a wrong
/// balance -- which is the right behaviour and a miserable thing to debug from a container
/// log at the end of a database reset.
/// <para>
/// So they are checked here, against the real files, where the failure names the expense.
/// </para>
/// </remarks>
public class SeededReceiptTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string SeedData([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        // Up from tests/GroupSplit.Seeder.Test/Seeding to the repo root, rather than from the
        // test assembly's output: the files are the sources, and nothing copies them.
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

        return Path.Combine(root, "src", "GroupSplit.Seeder", "SeedData");
    }

    private static List<TransactionSeedDto> Transactions() =>
        JsonSerializer.Deserialize<List<TransactionSeedDto>>(
            File.ReadAllText(Path.Combine(SeedData(), "transactions.json")), Options)!;

    private static List<CategorySeedDto> Categories() =>
        JsonSerializer.Deserialize<List<CategorySeedDto>>(
            File.ReadAllText(Path.Combine(SeedData(), "categories.json")), Options)!;

    /// <summary>
    /// A bill has to be the expense's own money, and the division refuses it otherwise --
    /// naming both figures, after the seeder has already written everything before it.
    /// </summary>
    [Fact]
    public void Every_seeded_bill_comes_to_its_expense_amount()
    {
        foreach (var expense in Transactions().Where(tx => tx.Receipt is not null))
        {
            var bill = expense.Receipt!;
            var total = bill.Items.Sum(line => line.Price) + bill.Tax + bill.Tip;

            Assert.True(total == expense.Amount,
                $"Seeded expense '{expense.Name}' ({expense.Id}) has a bill coming to {total}, "
                + $"but the expense is {expense.Amount}.");
        }
    }

    /// <summary>
    /// An itemised rule divides by the bill and by nothing else, so an expense filed under
    /// one without a bill cannot be divided at all.
    /// </summary>
    [Fact]
    public void Every_expense_under_an_itemized_category_has_a_bill()
    {
        var itemized = Categories()
            .Where(category => category.SplitRule is ItemizedSplitRuleDto)
            .Select(category => category.Id)
            .ToHashSet();

        // The guard is only worth anything if the demo data actually exercises the kind.
        Assert.NotEmpty(itemized);

        foreach (var expense in Transactions()
                     .Where(tx => tx.CategoryId is { } id && itemized.Contains(id)))
        {
            Assert.True(expense.Receipt is not null,
                $"Seeded expense '{expense.Name}' ({expense.Id}) is filed under a category that "
                + "divides by the bill, and has no bill on it.");
        }
    }

    /// <summary>
    /// Every line either names somebody or says it was the table's. A line that does neither
    /// is the one thing that stops a bill being divided at all.
    /// </summary>
    [Fact]
    public void Every_line_of_every_seeded_bill_is_accounted_for()
    {
        foreach (var expense in Transactions().Where(tx => tx.Receipt is not null))
        {
            foreach (var line in expense.Receipt!.Items)
            {
                Assert.True(line.Shared || line.Had.Count > 0,
                    $"'{line.Name}' on seeded expense '{expense.Name}' ({expense.Id}) "
                    + "names nobody and is not marked Shared, so the bill cannot be divided.");
            }
        }
    }

    /// <summary>
    /// A line cannot be both the table's and somebody's: Shared names nobody by design, and
    /// the claims beside it would be stored and never read.
    /// </summary>
    [Fact]
    public void No_seeded_line_is_both_shared_and_claimed()
    {
        foreach (var expense in Transactions().Where(tx => tx.Receipt is not null))
        {
            foreach (var line in expense.Receipt!.Items.Where(line => line.Shared))
            {
                Assert.True(line.Had.Count == 0,
                    $"'{line.Name}' on seeded expense '{expense.Name}' ({expense.Id}) is "
                    + "marked Shared and also names people; the names would be ignored.");
            }
        }
    }
}

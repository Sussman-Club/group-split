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
/// the run. Several ways, all of which stop the seeder dead rather than producing a wrong
/// balance -- which is the right behaviour and a miserable thing to debug from a container
/// log at the end of a database reset.
/// <para>
/// So they are checked here, against the real files, where the failure names the bill.
/// </para>
/// <para>
/// Both files that can carry one are checked together: a bill typed onto an expense and a
/// bill typed against a bank row are the same piece of paper, and what makes either of them
/// undividable is the same thing.
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

    private static List<T> Read<T>(string file) =>
        JsonSerializer.Deserialize<List<T>>(
            File.ReadAllText(Path.Combine(SeedData(), file)), Options)!;

    private static List<TransactionSeedDto> Transactions() =>
        Read<TransactionSeedDto>("transactions.json");

    private static List<CategorySeedDto> Categories() => Read<CategorySeedDto>("categories.json");

    /// <summary>
    /// Every seeded bill, wherever it is written, with the money it is a bill for.
    /// </summary>
    /// <param name="Where">
    /// What to say when one of these fails, which is the only reason the sequence carries a
    /// string at all: a figure that does not add up is useless without the line of the file
    /// to go and look at.
    /// </param>
    private static IEnumerable<(string Where, decimal Charge, ReceiptSeedDto Bill)> Bills()
    {
        foreach (var expense in Transactions().Where(tx => tx.Receipt is not null))
            yield return ($"expense '{expense.Name}' ({expense.Id})", expense.Amount, expense.Receipt!);

        var rows = Read<BankConnectionSeedDto>("bank-connections.json")
            .SelectMany(connection => connection.Accounts)
            .SelectMany(account => account.Transactions)
            .Where(row => row.Receipt is not null);

        foreach (var row in rows)
            yield return ($"bank row '{row.Description}' ({row.Id})", row.Amount, row.Receipt!);
    }

    /// <summary>
    /// A bill has to be the money it is a bill for, and both dividing an expense and
    /// splitting a charge refuse it otherwise -- naming both figures, after the seeder has
    /// already written everything before it.
    /// </summary>
    [Fact]
    public void Every_seeded_bill_comes_to_the_charge_it_is_a_bill_for()
    {
        foreach (var (where, charge, bill) in Bills())
        {
            var total = bill.Items.Sum(line => line.Price) + bill.Tax + bill.Tip;

            Assert.True(total == charge,
                $"Seeded {where} has a bill coming to {total}, but the charge is {charge}.");
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
        foreach (var (where, _, bill) in Bills())
        {
            foreach (var line in bill.Items)
            {
                Assert.True(line.Shared || line.Had.Count > 0,
                    $"'{line.Name}' on seeded {where} names nobody and is not marked Shared, "
                    + "so the bill cannot be divided.");
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
        foreach (var (where, _, bill) in Bills())
        {
            foreach (var line in bill.Items.Where(line => line.Shared))
            {
                Assert.True(line.Had.Count == 0,
                    $"'{line.Name}' on seeded {where} is marked Shared and also names people; "
                    + "the names would be ignored.");
            }
        }
    }

    /// <summary>
    /// A bill that charges tax marks something taxable.
    /// </summary>
    /// <remarks>
    /// Checked here precisely because nothing else would say anything. Tax is weighed across
    /// the taxable lines, and <c>ReceiptSplitCalculator</c> falls back to weighing it across
    /// every line when none is taxable -- which keeps a real bill dividing rather than
    /// refusing on a transcription error, and quietly does the one thing <c>Taxable</c> was
    /// added to prevent. A seed file that hit that fallback would divide, balance, and be
    /// wrong, with the demo showing tax on the exempt groceries.
    /// </remarks>
    [Fact]
    public void No_seeded_bill_charges_tax_with_nothing_taxable_on_it()
    {
        foreach (var (where, _, bill) in Bills().Where(entry => entry.Bill.Tax != 0))
        {
            Assert.True(bill.Items.Any(line => line.Taxable),
                $"Seeded {where} charges {bill.Tax} of tax and marks every line exempt, so the "
                + "tax would fall back onto every line, exempt ones included.");
        }
    }
}

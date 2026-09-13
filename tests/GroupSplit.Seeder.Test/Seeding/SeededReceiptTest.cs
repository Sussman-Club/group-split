using System.Text.Json;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
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
    /// Every line of a bill that is going to be divided by names somebody.
    /// </summary>
    /// <remarks>
    /// Claims are the only thing that says how a line divides, so a line naming nobody cannot
    /// be divided -- which the API refuses by name. Only the expenses' bills are checked: a
    /// bill on an unfiled bank row is there to be split into parts, and a part whose category
    /// divides evenly never reads the lines at all.
    /// </remarks>
    [Fact]
    public void Every_line_of_every_seeded_expenses_bill_names_somebody()
    {
        foreach (var expense in Transactions().Where(tx => tx.Receipt is not null))
        {
            foreach (var line in expense.Receipt!.Items)
            {
                Assert.True(line.Had.Count > 0,
                    $"'{line.Name}' on seeded expense '{expense.Name}' ({expense.Id}) names "
                    + "nobody, so the bill cannot be divided.");
            }
        }
    }

    /// <summary>
    /// Every seeded bill that is going to be divided actually divides, and comes to the
    /// expense exactly.
    /// </summary>
    /// <remarks>
    /// The checks above read the file; this runs the arithmetic the app runs -- the tax
    /// weighed over the lines it was charged on, the tip over all of them, each share
    /// truncated to the cent and the remainder going to the payer. A seed file can satisfy
    /// every one of the other assertions and still produce shares that do not sum to the
    /// charge, and the splitter refuses those: the run would stop somewhere in the middle of
    /// a thousand expenses with a figure and no line number.
    /// </remarks>
    [Fact]
    public void Every_seeded_bill_that_will_be_divided_divides_to_the_expense()
    {
        var itemized = Categories()
            .Where(category => category.SplitRule is ItemizedSplitRuleDto)
            .Select(category => category.Id)
            .ToHashSet();

        var divided = Transactions()
            .Where(tx => tx.Receipt is not null)
            .Where(tx => tx.CategoryId is { } id && itemized.Contains(id))
            .ToList();

        // A guard on the guard. Everything below is a loop over the seed file, and a loop
        // over nothing passes: the day somebody points the last itemised category somewhere
        // else, this should say so rather than go quietly green.
        Assert.NotEmpty(divided);

        foreach (var expense in divided)
        {
            var receipt = Receipt(expense.Receipt!, expense.Id);

            // Everybody the bill names, which is who a seeded expense is divided between.
            var participants = expense.Receipt!.Items
                .SelectMany(line => line.Had.Keys)
                .Append(expense.PayerId)
                .Distinct()
                .ToList();

            var shares = ReceiptSplitCalculator.Divide(
                receipt, expense.Id, expense.PayerId, participants);

            Assert.True(shares.Sum(share => share.Amount) == expense.Amount,
                $"Seeded expense '{expense.Name}' ({expense.Id}) divides by its bill into shares "
                + $"coming to {shares.Sum(share => share.Amount)}, but the expense is "
                + $"{expense.Amount}.");
        }
    }

    /// <summary>
    /// The bill as the seeder builds it, which is what the division is handed.
    /// </summary>
    /// <remarks>
    /// Built here rather than through <c>SeededBill</c>, which is internal to the seeder.
    /// The two have to agree on the same handful of derivations -- the lines are the
    /// subtotal, the extras are on top -- and a disagreement would show up as this test
    /// passing over a file the seeder then refuses.
    /// </remarks>
    private static Receipt Receipt(ReceiptSeedDto dto, Guid expenseId)
    {
        var subtotal = dto.Items.Sum(line => line.Price);

        var receipt = new Receipt
        {
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
                NormalizedName = line.Name.Trim().ToLowerInvariant(),
                TotalPrice = line.Price,
                Quantity = line.Quantity,
                TaxAmount = line.Tax,
                ExpenseId = expenseId
            };

            foreach (var (userId, weight) in line.Had)
                item.Claims.Add(new ReceiptItemClaim { UserId = userId, Weight = weight });

            receipt.Items.Add(item);
        }

        return receipt;
    }

    /// <summary>
    /// And no bill anywhere else says who had what.
    /// </summary>
    /// <remarks>
    /// The other half of the rule above, and the one the seeder enforces at run time: the
    /// itemised rule is the only thing that ever reads a claim, so a bill under a category
    /// that divides evenly -- or on a charge nobody has filed -- would store claims nothing
    /// would look at. Checked against the files as well as in the seeder, because the seeder
    /// says which line of which bill only after everything before it has been written.
    /// </remarks>
    [Fact]
    public void Only_a_bill_its_expense_divides_by_says_who_had_what()
    {
        var itemized = Categories()
            .Where(category => category.SplitRule is ItemizedSplitRuleDto)
            .Select(category => category.Id)
            .ToHashSet();

        var elsewhere = Transactions()
            .Where(tx => tx.Receipt is not null)
            .Where(tx => tx.CategoryId is not { } id || !itemized.Contains(id));

        foreach (var expense in elsewhere)
        {
            var claimed = expense.Receipt!.Items.Where(line => line.Had.Count > 0).ToList();

            Assert.True(claimed.Count == 0,
                $"Seeded expense '{expense.Name}' ({expense.Id}) is not split by its bill, but "
                + $"its bill says who had {string.Join(", ", claimed.Select(line => line.Name))}.");
        }

        var rows = Read<BankConnectionSeedDto>("bank-connections.json")
            .SelectMany(connection => connection.Accounts)
            .SelectMany(account => account.Transactions)
            .Where(row => row.Receipt is not null);

        foreach (var row in rows)
        {
            var claimed = row.Receipt!.Items.Where(line => line.Had.Count > 0).ToList();

            Assert.True(claimed.Count == 0,
                $"Seeded bank row '{row.Description}' ({row.Id}) is nobody's expense yet, but "
                + $"its bill says who had {string.Join(", ", claimed.Select(line => line.Name))}.");
        }
    }

    /// <summary>
    /// A bill's lines account for the tax at the bottom of it.
    /// </summary>
    /// <remarks>
    /// The tax lives on the lines, so a bill that charges some has to say which lines carried
    /// it -- and the division refuses one that does not, by name. This replaced a subtler
    /// check on a subtler failure: the tax used to be one figure spread over whatever a
    /// boolean marked, with a fallback to spreading it over everything when nothing was
    /// marked, so a seed file that flagged nothing divided, balanced, and put tax on the
    /// exempt groceries with no error anywhere.
    /// </remarks>
    [Fact]
    public void Every_seeded_bills_lines_account_for_its_tax()
    {
        foreach (var (where, _, bill) in Bills())
        {
            var lines = bill.Items.Sum(line => line.Tax);

            Assert.True(lines == bill.Tax,
                $"Seeded {where} charges {bill.Tax} of tax, but its lines carry {lines}.");
        }
    }
}

using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The slip, and what it says when one charge was filed as several expenses.
/// </summary>
/// <remarks>
/// A split charge leaves its parts sharing one piece of paper, and the sheet was drawing all
/// of it under each of them: a $53.54 expense under a $158.07 bill, with nothing to say which
/// of the six lines it was made of. These pin the marking, and the two counts it changed --
/// the lines still wanting somebody, which the division only ever asks of this part.
/// </remarks>
public class BillSheetTest : ComponentTest
{
    private static readonly Guid Mine = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Theirs = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>Every line of an ordinary bill is the expense's, so nothing is faded.</summary>
    [Fact]
    public void A_bill_that_is_all_one_purchase_marks_nothing()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: false))
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        Assert.Empty(page.FindAll(".gs-slip-line.is-elsewhere"));
        Assert.DoesNotContain("on this expense", page.Markup);
    }

    /// <summary>
    /// On a shared bill the other parts stay on the paper and are faded, which is what
    /// somebody checking a receipt against a charge needs: the lines are really there, they
    /// are just not what this expense is made of.
    /// </summary>
    [Fact]
    public void The_rest_of_a_split_charge_stays_on_the_paper_and_is_faded()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: true))
            // The count sits on the merchant line, which is only printed when there is one.
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        Assert.Equal(3, page.FindAll(".gs-slip-line").Count);
        Assert.Equal(1, page.FindAll(".gs-slip-line.is-elsewhere").Count);
        Assert.Contains("2 on this expense", page.Markup);
    }

    /// <summary>
    /// The expense's own figure, beside the one the card was charged. Without it a shared
    /// bill reads as a receipt that does not match the expense it was opened from.
    /// </summary>
    [Fact]
    public void A_shared_bill_prints_what_this_expense_came_to()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: true))
            .Add(sheet => sheet.PartAmount, 24m));

        var part = page.Find(".gs-slip-totals .is-part");

        Assert.Contains("This expense", part.TextContent);
        Assert.Contains("24", part.TextContent);
    }

    /// <summary>And never on a bill that is one purchase: there is nothing to tell apart.</summary>
    [Fact]
    public void An_ordinary_bill_prints_its_total_and_no_part()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: false))
            .Add(sheet => sheet.PartAmount, 24m));

        Assert.Empty(page.FindAll(".gs-slip-totals .is-part"));
    }

    /// <summary>
    /// A line belonging to nobody in somebody else's half of the charge is not this
    /// expense's to answer for -- which is the rule the division itself follows.
    /// </summary>
    [Fact]
    public void Lines_wanting_somebody_are_counted_over_this_expenses_part_only()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: true, unclaimedElsewhere: true)));

        // One of this expense's two lines wants somebody; the other part's unclaimed line
        // is not counted and its row is not marked.
        Assert.Contains("1 of 2 lines", page.Markup);
        Assert.Equal(1, page.FindAll(".gs-slip-line.is-unclaimed").Count);
    }

    /// <summary>
    /// Two identical items filed as two expenses stay two rows. Folded, one row would be
    /// half this expense's and half not, and no marking could then be true of it.
    /// </summary>
    [Fact]
    public void Identical_lines_in_different_parts_are_not_folded_together()
    {
        var receipt = Receipt(
            Line("Kitchen roll", 11.80m, Mine),
            Line("Kitchen roll", 11.80m, Theirs));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt));

        Assert.Equal(2, page.FindAll(".gs-slip-line").Count);
    }

    /// <summary>
    /// The "with nobody" toggle shows exactly what its own label counted.
    /// </summary>
    /// <remarks>
    /// The count was the part's and the filter behind it was the whole paper's, so the
    /// button read "1 with nobody" and then showed two rows -- the second being a line this
    /// expense is not answerable for and cannot do anything about.
    /// </remarks>
    [Fact]
    public void The_filter_for_lines_wanting_somebody_shows_what_its_label_counted()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: true, unclaimedElsewhere: true))
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        var toggle = page.FindAll("button").Single(button => button.TextContent.Contains("with nobody"));

        Assert.Contains("1 with nobody", toggle.TextContent);

        toggle.Click();

        Assert.Equal(1, page.FindAll(".gs-slip-line").Count);
    }

    /// <summary>
    /// "Showing 2 of 6" counts lines on both sides of the "of".
    /// </summary>
    /// <remarks>
    /// It summed the folded rows' QUANTITIES against a count of the bill's LINES, so the
    /// warehouse bill this app ships -- which has a line of twelve kitchen rolls -- read
    /// "Showing 15 of 6" as soon as anybody typed in the search box.
    /// </remarks>
    [Fact]
    public void What_is_showing_is_counted_in_lines_and_not_in_quantities()
    {
        // The shipped warehouse charge: kitchen roll by the dozen on the flat's half, the
        // clothes on somebody's own.
        var receipt = Receipt(
            Line("Kitchen roll", 11.80m, Mine, quantity: 12),
            Line("Rotisserie chicken", 8.99m, Mine),
            Line("Fleece jacket", 34.99m, Theirs));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt)
            .Add(sheet => sheet.Title, "Costco"));

        page.FindAll("button").Single(button => button.TextContent.Contains("Only this expense")).Click();

        // Two of the bill's three lines, not the thirteen items they came to.
        Assert.Contains("Showing 2 of 3", page.Markup);
    }

    // ---- a bill nothing divides by, which is most of them ------------------------------

    /// <summary>
    /// A line naming nobody is silent where nothing will ever read the claims.
    /// </summary>
    /// <remarks>
    /// The mainline case, and the one the fixture here could not reach: every bill on a
    /// charge still in the inbox comes back with this false, as does every bill under a
    /// category that divides evenly. The itemised rule is the only thing that reads a claim,
    /// so on any other bill a line with nobody on it is what a receipt looks like -- and
    /// saying "nobody yet" against it is how a screen teaches people to ignore it.
    /// </remarks>
    [Fact]
    public void A_bill_nothing_divides_by_says_nothing_about_lines_naming_nobody()
    {
        var receipt = Receipt(dividesItsExpense: false, tax: 0m, tip: 0m,
            Line("Rotisserie chicken", 8.99m, Mine, claimed: false),
            Line("Fleece jacket", 34.99m, Mine, claimed: false));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt)
            .Add(sheet => sheet.Title, "Costco"));

        Assert.DoesNotContain("nobody", page.Markup);
        Assert.Empty(page.FindAll(".gs-slip-line.is-unclaimed"));

        // And no tools at all: a six-line bill with nothing to answer is read by looking.
        Assert.Empty(page.FindAll(".gs-slip-tools"));
    }

    /// <summary>And under the rule that does read them, it asks.</summary>
    [Fact]
    public void A_bill_its_expense_divides_by_asks_who_had_the_unclaimed_lines()
    {
        var receipt = Receipt(dividesItsExpense: true, tax: 0m, tip: 0m,
            Line("Bistecca", 28.00m, Mine, claimed: true),
            Line("Vongole", 19.50m, Mine, claimed: false));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt)
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        Assert.Contains("nobody yet", page.Markup);
        Assert.Contains("1 of 2 lines", page.Markup);
        Assert.Equal(1, page.FindAll(".gs-slip-line.is-unclaimed").Count);
    }

    // ---- the extras --------------------------------------------------------------------

    /// <summary>
    /// Tax and tip are printed when there are any, and the exempt lines are marked.
    /// </summary>
    /// <remarks>
    /// Every bill in this fixture used to charge no tax and mark every line taxable, so the
    /// margin letter and the legend that explains it -- the slip's whole account of why one
    /// line was charged tax and its neighbour was not -- were never rendered.
    /// </remarks>
    [Fact]
    public void A_bill_that_charges_tax_marks_the_lines_it_was_not_charged_on()
    {
        var receipt = Receipt(dividesItsExpense: true, tax: 4.00m, tip: 6.00m,
            Line("Rotisserie chicken", 8.99m, Mine, taxable: false),
            Line("Fleece jacket", 34.99m, Mine));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt)
            .Add(sheet => sheet.Title, "Costco"));

        var marks = page.FindAll(".gs-slip-mark")
            .Where(mark => !string.IsNullOrWhiteSpace(mark.TextContent))
            .ToList();

        Assert.Equal("N", Assert.Single(marks).TextContent.Trim());
        Assert.Contains("no tax charged on this line", page.Markup);

        var totals = page.Find(".gs-slip-totals").TextContent;

        Assert.Contains("Tax", totals);
        Assert.Contains("Tip", totals);
    }

    /// <summary>
    /// And a bill with neither says neither: two rows of 0.00 are not information.
    /// </summary>
    [Fact]
    public void A_bill_with_no_tax_and_no_tip_prints_neither()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(shared: false))
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        var totals = page.Find(".gs-slip-totals").TextContent;

        Assert.DoesNotContain("Tax", totals);
        Assert.DoesNotContain("Tip", totals);
        Assert.Contains("Total", totals);

        // The legend goes with the mark: with no tax charged, nothing is exempt from it.
        Assert.DoesNotContain("no tax charged", page.Markup);
    }

    /// <summary>
    /// One bill: two lines this expense's, one the other part's -- optionally with a line
    /// somewhere on it that nobody has claimed.
    /// </summary>
    private static ReceiptResponse Bill(bool shared, bool unclaimedElsewhere = false)
    {
        var other = shared ? Theirs : Mine;

        return Receipt(
            Line("Bistecca", 28.00m, Mine, claimed: true),
            Line("Vongole", 19.50m, Mine, claimed: false),
            Line("Polpo", 21.00m, other, claimed: !unclaimedElsewhere));
    }

    private static ReceiptResponse Receipt(params ReceiptItemResponse[] items) =>
        Receipt(dividesItsExpense: true, tax: 0m, tip: 0m, items);

    private static ReceiptResponse Receipt(
        bool dividesItsExpense, decimal tax, decimal tip, params ReceiptItemResponse[] items) =>
        new(
            Guid.NewGuid(),
            ExpenseId: Mine,
            BankTransactionId: null,
            Subtotal: items.Sum(line => line.TotalPrice),
            Tax: tax,
            Tip: tip,
            Total: items.Sum(line => line.TotalPrice) + tax + tip,
            UnclaimedItemCount: items.Count(line => line.Claims.Count == 0),
            CanDivide: false,
            DividesItsExpense: dividesItsExpense,
            Items: items);

    private static ReceiptItemResponse Line(
        string name, decimal price, Guid expense,
        bool claimed = true, decimal quantity = 1, bool taxable = true) =>
        new(
            Guid.NewGuid(),
            name,
            UnitPrice: decimal.Round(price / quantity, 2),
            Quantity: quantity,
            TotalPrice: price,
            IsTaxable: taxable,
            ExpenseId: expense,
            Claims: claimed
                ? [new ReceiptClaimResponse(Mine, "Anabel", 1, price)]
                : []);
}

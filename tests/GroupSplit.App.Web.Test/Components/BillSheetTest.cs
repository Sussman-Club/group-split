using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The slip: what it prints, what it counts, and what it says about the rest of a charge it
/// is only one part of.
/// </summary>
/// <remarks>
/// A split charge leaves several expenses sharing one piece of paper. The sheet used to draw
/// all of it under each of them -- a 53.54 expense under a 158.07 bill, with every other
/// part's items named and priced and its claimants listed. The reading is scoped to the part
/// now and the rest arrives as a count and a figure, which is what these pin.
/// </remarks>
public class BillSheetTest : ComponentTest
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");

    // ---- one charge, several expenses --------------------------------------------------

    /// <summary>An ordinary bill is the whole of its expense, so there is nothing to say.</summary>
    [Fact]
    public void A_bill_that_is_all_one_purchase_says_nothing_about_other_parts()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill())
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        Assert.Empty(page.FindAll(".gs-slip-line.is-elsewhere"));
        Assert.DoesNotContain("on this charge", page.Markup);
    }

    /// <summary>
    /// The rest of a split charge is one line, counted and totalled and not named.
    /// </summary>
    /// <remarks>
    /// It answers the question the reader actually has -- why does this bill come to more
    /// than the expense I opened it from -- without telling them what was bought on somebody
    /// else's half of the same card charge, which can be another group's entirely.
    /// </remarks>
    [Fact]
    public void The_rest_of_a_split_charge_is_one_line_that_names_nothing()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(elsewhereLines: 4, elsewhereTotal: 104.53m))
            .Add(sheet => sheet.Title, "Costco"));

        var line = page.Find(".gs-slip-line.is-elsewhere");

        Assert.Contains("4 more lines on this charge", line.TextContent);
        Assert.Contains("104.53", line.TextContent);
        Assert.Contains("filed as other expenses", line.TextContent);

        // On the slip with the rest, because it is a line of this bill and the reader adds
        // it to the ones above to reach the total.
        Assert.Equal(3, page.FindAll(".gs-slip-lines .gs-slip-line").Count);
    }

    /// <summary>And it reads as one line when it is one.</summary>
    [Fact]
    public void One_line_elsewhere_is_described_in_the_singular()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(elsewhereLines: 1, elsewhereTotal: 34.99m))
            .Add(sheet => sheet.Title, "Costco"));

        var line = page.Find(".gs-slip-line.is-elsewhere");

        Assert.Contains("1 more line on this charge", line.TextContent);
        Assert.Contains("filed as another expense", line.TextContent);
    }

    /// <summary>
    /// The expense's own figure, beside the one the card was charged.
    /// </summary>
    /// <remarks>
    /// The only place the two can be seen together. Without it a shared bill reads as a
    /// receipt that does not match the expense it was opened from, which is what it looks
    /// like and is the thing somebody would otherwise go and check.
    /// </remarks>
    [Fact]
    public void A_shared_bill_prints_what_this_expense_came_to()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(elsewhereLines: 4, elsewhereTotal: 104.53m))
            .Add(sheet => sheet.PartAmount, 53.54m));

        var part = page.Find(".gs-slip-totals .is-part");

        Assert.Contains("This expense", part.TextContent);
        Assert.Contains("53.54", part.TextContent);
    }

    /// <summary>And never on a bill that is one purchase: there is nothing to tell apart.</summary>
    [Fact]
    public void An_ordinary_bill_prints_its_total_and_no_part()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill())
            .Add(sheet => sheet.PartAmount, 47.50m));

        Assert.Empty(page.FindAll(".gs-slip-totals .is-part"));
    }

    // ---- lines nobody has claimed ------------------------------------------------------

    /// <summary>
    /// The "with nobody" toggle shows exactly what its own label counted.
    /// </summary>
    /// <remarks>
    /// The count was the part's and the filter behind it was the whole paper's, so the button
    /// read "1 with nobody" and then showed two rows -- the second a line this expense is not
    /// answerable for and can do nothing about. Both are the part now, and one predicate
    /// serves the count, the filter and the row's marking so they cannot drift again.
    /// </remarks>
    [Fact]
    public void The_filter_for_lines_wanting_somebody_shows_what_its_label_counted()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(claimed: false))
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        var toggle = page.FindAll("button").Single(button => button.TextContent.Contains("with nobody"));

        Assert.Contains("1 with nobody", toggle.TextContent);

        toggle.Click();

        Assert.Equal(1, page.FindAll(".gs-slip-lines .gs-slip-line").Count);
    }

    /// <summary>The toggle says whether it is on, which is how the repo marks every other one.</summary>
    [Fact]
    public void The_filter_toggle_reports_whether_it_is_pressed()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(claimed: false))
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        var toggle = page.FindAll("button").Single(button => button.TextContent.Contains("with nobody"));

        Assert.Equal("false", toggle.GetAttribute("aria-pressed"));

        toggle.Click();

        Assert.Equal("true", page.FindAll("button")
            .Single(button => button.TextContent.Contains("with nobody"))
            .GetAttribute("aria-pressed"));
    }

    /// <summary>
    /// "Showing 2 of 6" counts lines on both sides of the "of".
    /// </summary>
    /// <remarks>
    /// It summed the folded rows' QUANTITIES against a count of the bill's LINES, so the
    /// warehouse charge this app ships -- which has a line of twelve kitchen rolls -- read
    /// "Showing 13 of 3" the moment anybody narrowed it.
    /// </remarks>
    [Fact]
    public void What_is_showing_is_counted_in_lines_and_not_in_quantities()
    {
        var receipt = Receipt(
            dividesItsExpense: true,
            Line("Kitchen roll", 11.80m, quantity: 12),
            Line("Rotisserie chicken", 8.99m),
            Line("Fleece jacket", 34.99m, claimed: false));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt)
            .Add(sheet => sheet.Title, "Costco"));

        page.FindAll("button").Single(button => button.TextContent.Contains("with nobody")).Click();

        // One of the bill's three lines, not one of its fourteen items.
        Assert.Contains("Showing 1 of 3", page.Markup);
    }

    // ---- a bill nothing divides by, which is most of them ------------------------------

    /// <summary>
    /// A line naming nobody is silent where nothing will ever read the claims.
    /// </summary>
    /// <remarks>
    /// The mainline case. Every bill on a charge still in the inbox comes back with this
    /// false, as does every bill under a category that divides evenly. The itemised rule is
    /// the only thing that reads a claim, so on any other bill a line with nobody on it is
    /// what a receipt looks like -- and saying "nobody yet" against it is how a screen
    /// teaches people to ignore it.
    /// </remarks>
    [Fact]
    public void A_bill_nothing_divides_by_says_nothing_about_lines_naming_nobody()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(dividesItsExpense: false, claimed: false))
            .Add(sheet => sheet.Title, "Costco"));

        Assert.DoesNotContain("nobody", page.Markup);
        Assert.Empty(page.FindAll(".gs-slip-line.is-unclaimed"));

        // And no tools at all: a short bill with nothing to answer is read by looking.
        Assert.Empty(page.FindAll(".gs-slip-tools"));
    }

    /// <summary>And under the rule that does read them, it asks -- once, in the singular.</summary>
    [Fact]
    public void A_bill_its_expense_divides_by_asks_who_had_the_unclaimed_lines()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill(claimed: false))
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        Assert.Contains("1 of 2 lines has nobody on it", page.Markup);

        // "...who had it", agreeing with the sentence before it. It said "them" whatever the
        // count, under a sentence that had just said "it" -- the commonest case of all.
        Assert.Contains("who had it", page.Markup);
        Assert.DoesNotContain("who had them", page.Markup);
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
        var receipt = Receipt(
            dividesItsExpense: true, tax: 4.00m, tip: 6.00m,
            Line("Rotisserie chicken", 8.99m, taxable: false),
            Line("Fleece jacket", 34.99m));

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

    /// <summary>And a bill with neither says neither: two rows of 0.00 are not information.</summary>
    [Fact]
    public void A_bill_with_no_tax_and_no_tip_prints_neither()
    {
        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, Bill())
            .Add(sheet => sheet.Title, "Trattoria da Enzo"));

        var totals = page.Find(".gs-slip-totals").TextContent;

        Assert.DoesNotContain("Tax", totals);
        Assert.DoesNotContain("Tip", totals);
        Assert.Contains("Total", totals);

        // The legend goes with the mark: with no tax charged, nothing is exempt from it.
        Assert.DoesNotContain("no tax charged", page.Markup);
    }

    /// <summary>
    /// Identical lines are folded, and told apart by anything that makes them different
    /// things.
    /// </summary>
    [Fact]
    public void Identical_lines_are_printed_once_with_a_count()
    {
        var receipt = Receipt(
            dividesItsExpense: true,
            Line("Fleece jacket", 59.99m),
            Line("Fleece jacket", 59.99m));

        var page = Render<BillSheet>(parameters => parameters
            .Add(sheet => sheet.Receipt, receipt)
            .Add(sheet => sheet.Title, "Costco"));

        var line = Assert.Single(page.FindAll(".gs-slip-lines .gs-slip-line"));

        Assert.Contains("2 @", line.TextContent);
        Assert.Contains("119.98", line.TextContent);
    }

    /// <summary>
    /// A restaurant bill: two lines, one of them optionally claimed, plus however much of the
    /// charge belongs to the other expenses filed from it.
    /// </summary>
    private static ReceiptResponse Bill(
        bool dividesItsExpense = true, bool claimed = true,
        int elsewhereLines = 0, decimal elsewhereTotal = 0m) =>
        Receipt(
            dividesItsExpense, 0m, 0m, elsewhereLines, elsewhereTotal,
            Line("Bistecca", 28.00m),
            Line("Vongole", 19.50m, claimed: claimed));

    private static ReceiptResponse Receipt(
        bool dividesItsExpense, params ReceiptItemResponse[] items) =>
        Receipt(dividesItsExpense, 0m, 0m, 0, 0m, items);

    private static ReceiptResponse Receipt(
        bool dividesItsExpense, decimal tax, decimal tip, params ReceiptItemResponse[] items) =>
        Receipt(dividesItsExpense, tax, tip, 0, 0m, items);

    /// <summary>
    /// One reading of a bill: this expense's lines, and how much of the paper is not them.
    /// </summary>
    /// <remarks>
    /// The API scopes <c>Items</c> to the part being read, so a shared bill arrives as this
    /// expense's lines plus a count and a total for the rest -- never the other parts' names,
    /// prices or claimants, which can belong to another group entirely.
    /// </remarks>
    private static ReceiptResponse Receipt(
        bool dividesItsExpense, decimal tax, decimal tip,
        int elsewhereLines, decimal elsewhereTotal, params ReceiptItemResponse[] items) =>
        new(
            Guid.NewGuid(),
            ExpenseId: Me,
            BankTransactionId: null,
            Subtotal: items.Sum(line => line.TotalPrice) + elsewhereTotal,
            Tax: tax,
            Tip: tip,
            Total: items.Sum(line => line.TotalPrice) + elsewhereTotal + tax + tip,
            UnclaimedItemCount: items.Count(line => line.Claims.Count == 0),
            CanDivide: false,
            DividesItsExpense: dividesItsExpense,
            ElsewhereItemCount: elsewhereLines,
            ElsewhereTotal: elsewhereTotal,
            Items: items);

    private static ReceiptItemResponse Line(
        string name, decimal price,
        bool claimed = true, decimal quantity = 1, bool taxable = true) =>
        new(
            Guid.NewGuid(),
            name,
            UnitPrice: decimal.Round(price / quantity, 2),
            Quantity: quantity,
            TotalPrice: price,
            IsTaxable: taxable,
            ExpenseId: Me,
            Claims: claimed
                ? [new ReceiptClaimResponse(Me, "Anabel", 1, price)]
                : []);
}

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
        new(
            Guid.NewGuid(),
            ExpenseId: Mine,
            BankTransactionId: null,
            Subtotal: items.Sum(line => line.TotalPrice),
            Tax: 0m,
            Tip: 0m,
            Total: items.Sum(line => line.TotalPrice),
            UnclaimedItemCount: items.Count(line => line.Claims.Count == 0),
            CanDivide: false,
            // The itemised rule, so the sheet asks about lines belonging to nobody at all.
            DividesItsExpense: true,
            Items: items);

    private static ReceiptItemResponse Line(
        string name, decimal price, Guid expense, bool claimed = true) =>
        new(
            Guid.NewGuid(),
            name,
            UnitPrice: price,
            Quantity: 1,
            TotalPrice: price,
            IsTaxable: true,
            ExpenseId: expense,
            Claims: claimed
                ? [new ReceiptClaimResponse(Mine, "Anabel", 1, price)]
                : []);
}

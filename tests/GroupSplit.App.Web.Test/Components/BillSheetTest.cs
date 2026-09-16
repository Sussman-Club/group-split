using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

public class BillSheetTest : ComponentTest
{
    private static ReceiptItemResponse Line(string name, decimal price, string? rule, decimal tax = 0,
        string? description = null) =>
        new ReceiptItemResponse(Guid.NewGuid(), name, price, 1, price, tax,
            rule is null ? null : Guid.NewGuid(), rule,
            rule is null ? null : new EvenSplitRuleDto()) with { Description = description };

    private static ReceiptResponse Bill(params ReceiptItemResponse[] items) =>
        new(Guid.NewGuid(), Guid.NewGuid(), items.Sum(i => i.TotalPrice), items.Sum(i => i.TaxAmount), 0,
            items.Sum(i => i.TotalPrice + i.TaxAmount), items.Count(i => i.SplitRuleVersionId is null),
            false, true, items);

    [Fact]
    public void Prints_item_rule_names_and_how_many_lines_still_want_one()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Pizza", 20, "Together"), Line("Wine", 10, null))));

        Assert.Contains("Together", page.Markup);
        Assert.Contains("no split rule yet", page.Markup);
        Assert.Contains("1 of 2 lines has no split rule", page.Markup);
        Assert.Contains("30.00", page.Markup);
    }

    /// <summary>
    /// A bill nothing divides by is a receipt, not a job left undone: every line on one names
    /// no rule and none of them is missing anything.
    /// </summary>
    [Fact]
    public void A_bill_that_divides_nothing_does_not_ask_for_rules()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Pizza", 20, null)) with { DividesItsExpense = false }));

        Assert.DoesNotContain("no split rule", page.Markup);
    }

    /// <summary>
    /// Tax is on the line now, so a bill charging two rates has to show which line carried
    /// which -- a single figure at the foot cannot say that. The lines carrying none get the
    /// letter a till prints in the margin, and the legend that explains it.
    /// </summary>
    [Fact]
    public void A_line_that_was_taxed_says_so_and_one_that_was_not_is_marked()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Food", 100, "Together", tax: 6), Line("Corkage", 20, "Together"))));

        Assert.Contains("tax $6.00", page.Markup);
        Assert.Contains("no tax charged on this line", page.Markup);
    }

    /// <summary>
    /// A bill with no tax at all has nothing to mark, so neither the margin nor the legend
    /// appears -- a column of markers on a receipt charging no tax explains nothing.
    /// </summary>
    [Fact]
    public void A_bill_with_no_tax_prints_no_tax_marks()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Food", 100, "Together"), Line("Corkage", 20, "Together"))));

        Assert.DoesNotContain("no tax charged on this line", page.Markup);
        Assert.DoesNotContain("Tax</dt>", page.Markup);
    }

    /// <summary>
    /// Identical lines fold into one row, which is the difference between reading a warehouse
    /// bill and scrolling past it. The row says how many and what one cost.
    /// </summary>
    [Fact]
    public void Identical_lines_print_once_with_a_count()
    {
        var rule = Guid.NewGuid();
        ReceiptItemResponse Jacket() =>
            new(Guid.NewGuid(), "Jacket", 59.99m, 1, 59.99m, 0, rule, "Together", new EvenSplitRuleDto());

        var page = Render<BillSheet>(p => p.Add(c => c.Receipt, Bill(Jacket(), Jacket())));

        Assert.Single(page.FindAll("li.gs-slip-line"));
        Assert.Contains("2 @ $59.99", page.Markup);
        Assert.Contains("119.98", page.Markup);
    }

    /// <summary>
    /// The same two lines divided by different rules stay two rows: telling those apart is
    /// the whole reason they are stored separately rather than as a quantity.
    /// </summary>
    [Fact]
    public void Lines_divided_by_different_rules_do_not_fold()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Jacket", 59.99m, "Ana"), Line("Jacket", 59.99m, "Together"))));

        Assert.Equal(2, page.FindAll("li.gs-slip-line").Count);
    }

    /// <summary>
    /// A short bill is read whole; a long one gets a box to find a line in. Twelve is where
    /// that starts, so eleven lines must not offer it.
    /// </summary>
    [Fact]
    public void Only_a_long_bill_offers_a_way_to_find_a_line()
    {
        var short11 = Enumerable.Range(1, 11).Select(n => Line($"Item {n}", 1, "Together")).ToArray();

        Assert.DoesNotContain("Find a line",
            Render<BillSheet>(p => p.Add(c => c.Receipt, Bill(short11))).Markup);
        Assert.Contains("Find a line",
            Render<BillSheet>(p => p.Add(c => c.Receipt,
                Bill([.. short11, Line("Item 12", 1, "Together")]))).Markup);
    }

    [Fact]
    public void Finding_a_line_hides_the_ones_that_do_not_match()
    {
        var lines = Enumerable.Range(1, 11).Select(n => Line($"Item {n}", 1, "Together"))
            .Append(Line("Birthday cake", 1, "Together")).ToArray();
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt, Bill(lines)));

        page.Find("input").Input("cake");

        page.WaitForAssertion(() => Assert.DoesNotContain("Item 1<", page.Markup));
        Assert.Contains("Birthday cake", page.Markup);
    }

    /// <summary>
    /// The totals are the whole bill whatever is filtered above them: a receipt adding up to
    /// something the card was never charged is worse than one that is hard to search.
    /// </summary>
    [Fact]
    public void A_filter_narrows_the_lines_and_leaves_the_totals_alone()
    {
        var lines = Enumerable.Range(1, 11).Select(n => Line($"Item {n}", 1, "Together"))
            .Append(Line("Birthday cake", 30, "Together")).ToArray();
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt, Bill(lines)));

        page.Find("input").Input("cake");

        page.WaitForAssertion(() => Assert.Contains("Showing 1 of 12", page.Markup));
        Assert.Contains("41.00", page.Markup);
    }

    [Fact]
    public void Source_receipt_text_is_visible_and_searchable()
    {
        var lines = Enumerable.Range(1, 11).Select(n => Line($"Item {n}", 1, "Together"))
            .Append(Line("Water", 7.98m, "Together", description: "KIRKLAND SIGNATURE WATER 40 PK"))
            .ToArray();
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt, Bill(lines)));

        Assert.Contains("KIRKLAND SIGNATURE WATER 40 PK", page.Markup);
        page.Find("input").Input("KIRKLAND");
        page.WaitForAssertion(() => Assert.Contains("Water", page.Markup));
    }
}

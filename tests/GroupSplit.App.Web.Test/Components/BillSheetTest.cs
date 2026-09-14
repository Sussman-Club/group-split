using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

public class BillSheetTest : ComponentTest
{
    private static ReceiptItemResponse Line(string name, decimal price, string? rule, decimal tax = 0) =>
        new(Guid.NewGuid(), name, price, 1, price, tax, rule is null ? null : Guid.NewGuid(), rule,
            rule is null ? null : new EvenSplitRuleDto());

    private static ReceiptResponse Bill(params ReceiptItemResponse[] items) =>
        new(Guid.NewGuid(), Guid.NewGuid(), items.Sum(i => i.TotalPrice), items.Sum(i => i.TaxAmount), 0,
            items.Sum(i => i.TotalPrice + i.TaxAmount), items.Count(i => i.SplitRuleVersionId is null),
            false, true, items);

    [Fact]
    public void Prints_item_rule_names_and_missing_rule_count()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Pizza", 20, "Together"), Line("Wine", 10, null))));

        Assert.Contains("Together", page.Markup);
        Assert.Contains("Choose a split rule", page.Markup);
        Assert.Contains("1 item(s) need a split rule", page.Markup);
        Assert.Contains("30.00", page.Markup);
    }

    /// <summary>
    /// Tax is on the line now, so a bill charging two rates has to show which line carried
    /// which -- a single figure at the foot cannot say that.
    /// </summary>
    [Fact]
    public void A_line_that_was_taxed_says_so_and_one_that_was_not_stays_quiet()
    {
        var page = Render<BillSheet>(p => p.Add(c => c.Receipt,
            Bill(Line("Food", 100, "Together", tax: 6), Line("Corkage", 20, "Together"))));

        Assert.Contains("Tax 6.00", page.Markup);
        Assert.DoesNotContain("Tax 0.00</div>", page.Markup);
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

        Assert.Contains("Birthday cake", page.Markup);
        Assert.DoesNotContain("Item 1<", page.Markup);
    }
}

using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The bill as the body of a split option: one line, one rule.
/// </summary>
/// <remarks>
/// The thing to hold onto here is what saves when. A line's rule is a fact about the receipt
/// and is written as it is picked; whether the expense divides by that receipt is a fact
/// about the expense and waits for Save. So there is no Apply button on this component at
/// all, and the tests below say so.
/// </remarks>
public class BillDivisionTest : ComponentTest
{
    private readonly Mock<ISplitRuleCommands> _rules = new();
    private readonly Mock<IReceiptCommands> _bills = new();
    private readonly Guid _group = Guid.NewGuid();
    private readonly Guid _expense = Guid.NewGuid();
    private readonly Guid _ruleId = Guid.NewGuid();
    private readonly Guid _versionId = Guid.NewGuid();

    public BillDivisionTest()
    {
        Services.AddSingleton(_rules.Object);
        Services.AddSingleton(_bills.Object);

        _rules.Setup(r => r.ForGroupAsync(_group, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SplitRuleResponse(_ruleId, _group, "Everyone evenly")]);

        _rules.Setup(r => r.GetAsync(_ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleDetailsResponse
            {
                Id = _ruleId, GroupId = _group, Name = "Everyone evenly",
                VersionId = _versionId, Definition = new EvenSplitRuleDto()
            });
    }

    private static ReceiptItemResponse Line(string name, decimal price, Guid? version, string? rule) =>
        new(Guid.NewGuid(), name, price, 1, price, 0, version, rule, rule is null ? null : new EvenSplitRuleDto());

    private ReceiptResponse Bill(params ReceiptItemResponse[] items) =>
        new(Guid.NewGuid(), _expense, items.Sum(i => i.TotalPrice), 0, 0, items.Sum(i => i.TotalPrice),
            items.Count(i => i.SplitRuleVersionId is null), false, true, items) { CanEdit = true };

    private IRenderedComponent<BillDivision> Open(ReceiptResponse bill, decimal? amount = null) =>
        Render<BillDivision>(p => p
            .Add(c => c.Receipt, bill)
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.GroupId, _group)
            .Add(c => c.Amount, amount ?? bill.Total));

    /// <summary>
    /// What is left to do, before the lines rather than under them: the count going down is
    /// the whole of the progress there is here.
    /// </summary>
    [Fact]
    public void It_counts_the_lines_still_to_answer_for()
    {
        var page = Open(Bill(
            Line("Bacalhau", 22, null, null),
            Line("Vinho verde", 18, Guid.NewGuid(), "Everyone evenly")));

        page.WaitForAssertion(() => Assert.Contains("1 of 2 lines", page.Markup));
        Assert.Contains("still to answer for", page.Markup);
    }

    /// <summary>
    /// Nothing here applies anything. The division reaches the expense through Save, so a
    /// button that moved money on this component would be a second commit on one screen --
    /// which is the whole reason the bill stopped being a dialog of its own.
    /// </summary>
    [Fact]
    public void It_offers_no_way_to_apply_anything()
    {
        var page = Open(Bill(Line("Bacalhau", 22, null, null)));

        page.WaitForAssertion(() => Assert.Contains("Bacalhau", page.Markup));
        Assert.DoesNotContain("Apply", page.Markup);
        Assert.DoesNotContain("Preview", page.Markup);
        Assert.Contains("Save the expense to divide it this way", page.Markup);
    }

    /// <summary>
    /// The division refuses outright when the paper and the expense disagree -- see
    /// ItemizedSplitRuleHandler, which raises ReceiptDoesNotAddUp. Said here rather than met
    /// at the Save button.
    /// </summary>
    [Fact]
    public void A_bill_that_does_not_match_the_expense_says_so_before_saving()
    {
        var page = Open(Bill(Line("Bacalhau", 22, Guid.NewGuid(), "Everyone evenly")), amount: 30m);

        page.WaitForAssertion(() => Assert.Contains("the bill is short by", page.Markup));
        Assert.Contains("$8.00", page.Markup);
    }

    [Fact]
    public void A_bill_that_matches_the_expense_says_nothing_about_it()
    {
        var page = Open(Bill(Line("Bacalhau", 22, Guid.NewGuid(), "Everyone evenly")));

        page.WaitForAssertion(() => Assert.Contains("Every line has a rule", page.Markup));
        Assert.DoesNotContain("the bill is short by", page.Markup);
        Assert.DoesNotContain("the bill is over by", page.Markup);
    }

    /// <summary>
    /// Pointing a line at a rule is written as it is picked, and the answer is the whole bill
    /// back -- so the count above the lines follows without a second read.
    /// </summary>
    [Fact]
    public async Task Choosing_a_rule_for_a_line_writes_it_and_raises_the_new_bill()
    {
        var before = Bill(Line("Bacalhau", 22, null, null));
        var after = Bill(Line("Bacalhau", 22, _versionId, "Everyone evenly"));

        _bills.Setup(b => b.SetRuleAsync(_expense, It.IsAny<Guid>(), _versionId)).ReturnsAsync(after);

        ReceiptResponse? raised = null;

        var page = Render<BillDivision>(p => p
            .Add(c => c.Receipt, before)
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.GroupId, _group)
            .Add(c => c.Amount, before.Total)
            .Add(c => c.ReceiptChanged, bill => raised = bill));

        // The line's control appearing is what says the rules finished loading. The option
        // text itself lives in a popover that is not open, so it is not in the markup.
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindComponents<MudSelect<Guid?>>()));

        // Through the binding the markup actually declares, rather than by reaching into the
        // component: this is the callback the line's select fires when somebody picks.
        var select = page.FindComponent<MudSelect<Guid?>>();
        await page.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(_versionId));

        page.WaitForAssertion(() => Assert.NotNull(raised));
        _bills.Verify(b => b.SetRuleAsync(_expense, before.Items[0].Id, _versionId), Times.Once);
    }

    /// <summary>
    /// The bill's own rule is never an option on one of its lines: a line dividing by "divide
    /// it by the bill" is the expense pointing at itself.
    /// </summary>
    [Fact]
    public void The_bill_rule_is_not_offered_as_a_rule_for_a_line()
    {
        var billRule = Guid.NewGuid();
        var billVersion = Guid.NewGuid();

        _rules.Setup(r => r.ForGroupAsync(_group, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SplitRuleResponse(_ruleId, _group, "Everyone evenly"),
                new SplitRuleResponse(billRule, _group, "Divide by the bill", DividesByBill: true)
            ]);

        _rules.Setup(r => r.GetAsync(billRule, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleDetailsResponse
            {
                Id = billRule, GroupId = _group, Name = "Divide by the bill",
                VersionId = billVersion, Definition = new ItemizedSplitRuleDto()
            });

        var page = Open(Bill(Line("Bacalhau", 22, null, null)));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindComponents<MudSelectItem<Guid?>>()));

        // By the version each option carries, not by its label: the labels live inside a
        // popover that is closed, so an option that is offered still renders no text.
        var offered = page.FindComponents<MudSelectItem<Guid?>>()
            .Select(item => item.Instance.Value)
            .ToList();

        Assert.Contains(_versionId, offered);
        Assert.DoesNotContain(billVersion, offered);
    }
}

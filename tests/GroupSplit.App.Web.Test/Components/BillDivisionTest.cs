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

    /// <summary>
    /// An edit to the amount of an expense whose bill came to the saved amount is one the
    /// save carries out, the difference on the tip -- so the screen says that, and not that
    /// the two have to match.
    /// </summary>
    [Fact]
    public void An_edited_amount_says_the_bill_follows_it_on_the_tip()
    {
        var page = Render<BillDivision>(p => p
            .Add(c => c.Receipt, Bill(Line("Bacalhau", 22, Guid.NewGuid(), "Everyone evenly")))
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.GroupId, _group)
            .Add(c => c.Amount, 25m)
            .Add(c => c.SavedAmount, 22m));

        page.WaitForAssertion(() => Assert.Contains("Saving changes the bill's tip from", page.Markup));
        Assert.Contains("$3.00", page.Markup);
        Assert.Contains("$25.00", page.Markup);
        Assert.DoesNotContain("They have to match", page.Markup);
    }

    /// <summary>
    /// Lowering it by more than the tip is refused by the save, and said before it.
    /// </summary>
    [Fact]
    public void A_lowered_amount_beyond_the_tip_says_to_edit_the_items()
    {
        var page = Render<BillDivision>(p => p
            .Add(c => c.Receipt, Bill(Line("Bacalhau", 22, Guid.NewGuid(), "Everyone evenly")))
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.GroupId, _group)
            .Add(c => c.Amount, 20m)
            .Add(c => c.SavedAmount, 22m));

        page.WaitForAssertion(() => Assert.Contains("Edit the receipt's items instead.", page.Markup));
        Assert.DoesNotContain("Saving changes the bill's tip", page.Markup);
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

    /// <summary>
    /// A receipt line is another expense split picker. It must not resurrect the built-in
    /// rule belonging to somebody who has left merely because the rule is still historical.
    /// </summary>
    [Fact]
    public void A_departed_members_built_in_rule_is_not_offered_for_a_line()
    {
        var departed = Guid.NewGuid();
        var departedRule = Guid.NewGuid();
        var departedVersion = Guid.NewGuid();

        _rules
            .Setup(r => r.ForGroupAsync(_group, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SplitRuleResponse(_ruleId, _group, "Everyone evenly"),
                new SplitRuleResponse(departedRule, _group, "All for departed", BuiltIn: true,
                    AllForUserId: departed)
            ]);

        _rules
            .Setup(r => r.GetAsync(departedRule, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleDetailsResponse
            {
                Id = departedRule, GroupId = _group, Name = "All for departed",
                VersionId = departedVersion, Definition = new SoleSplitRuleDto(departed)
            });

        var page = Open(Bill(Line("Bacalhau", 22, null, null)));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindComponents<MudSelectItem<Guid?>>()));

        var offered = page.FindComponents<MudSelectItem<Guid?>>()
            .Select(item => item.Instance.Value)
            .ToList();

        Assert.Contains(_versionId, offered);
        Assert.DoesNotContain(departedVersion, offered);
    }

    /// <summary>A rule for "Groceries" beside the group's Even, both offered for a line.</summary>
    private (Guid Rule, Guid Version) AGroceriesRule(SplitRuleDto definition)
    {
        var rule = Guid.NewGuid();
        var version = Guid.NewGuid();

        _rules.Setup(r => r.ForGroupAsync(_group, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SplitRuleResponse(_ruleId, _group, "Everyone evenly"),
                new SplitRuleResponse(rule, _group, "Groceries split")
            ]);

        _rules.Setup(r => r.GetAsync(rule, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SplitRuleDetailsResponse
            {
                Id = rule, GroupId = _group, Name = "Groceries split",
                VersionId = version, Definition = definition
            });

        return (rule, version);
    }

    /// <summary>
    /// Echoes the bill back with the line on the version it was given, the way the API
    /// answers a rule change.
    /// </summary>
    private void AnswerRuleChangesOn(ReceiptResponse bill)
    {
        _bills.Setup(b => b.SetRuleAsync(_expense, It.IsAny<Guid>(), It.IsAny<Guid?>()))
            .ReturnsAsync((Guid _, Guid itemId, Guid? version) =>
            {
                bill = bill with
                {
                    Items = [.. bill.Items.Select(item => item.Id == itemId
                        ? item with { SplitRuleVersionId = version, SplitRuleName = "set" }
                        : item)]
                };
                return bill;
            });
    }

    private IRenderedComponent<BillDivision> OpenUnder(ReceiptResponse bill, Guid? categoryRule,
        IReadOnlyList<UserInfo>? members = null) =>
        Render<BillDivision>(p => p
            .Add(c => c.Receipt, bill)
            .Add(c => c.TransactionId, _expense)
            .Add(c => c.GroupId, _group)
            .Add(c => c.Amount, bill.Total)
            .Add(c => c.Members, members ?? [])
            .Add(c => c.CategoryRuleId, categoryRule)
            .Add(c => c.CategoryName, "Groceries")
            .Add(c => c.EvenRuleId, _ruleId));

    /// <summary>
    /// Most of a bill divides the way the category does, so a line nobody answered starts on
    /// the category's rule -- and one somebody did answer is left exactly where it was.
    /// </summary>
    [Fact]
    public void Unanswered_lines_start_on_the_categorys_rule_and_say_so()
    {
        var (groceries, groceriesVersion) = AGroceriesRule(new EvenSplitRuleDto());
        var bill = Bill(
            Line("Bananas", 3, null, null),
            Line("Milk", 2, null, null),
            Line("Steak", 15, _versionId, "Everyone evenly"));
        AnswerRuleChangesOn(bill);

        var page = OpenUnder(bill, groceries);

        page.WaitForAssertion(() => Assert.Contains("Every line has a rule", page.Markup));
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[0].Id, groceriesVersion), Times.Once);
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[1].Id, groceriesVersion), Times.Once);
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[2].Id, It.IsAny<Guid?>()), Times.Never);
        Assert.Equal(2, page.FindAll(".gs-divide-line-note").Count(note => note.TextContent == "from Groceries"));
    }

    /// <summary>
    /// The pre-fill is a starting point: picking another rule for one line writes that one
    /// line, and its hint goes with it.
    /// </summary>
    [Fact]
    public async Task A_line_started_on_the_default_can_be_pointed_elsewhere()
    {
        var (groceries, groceriesVersion) = AGroceriesRule(new EvenSplitRuleDto());
        var bill = Bill(Line("Bananas", 3, null, null), Line("Wine", 12, null, null));
        AnswerRuleChangesOn(bill);

        var page = OpenUnder(bill, groceries);
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".gs-divide-line-note").Count));

        var wine = page.FindComponents<MudSelect<Guid?>>()[1];
        await page.InvokeAsync(() => wine.Instance.ValueChanged.InvokeAsync(_versionId));

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".gs-divide-line-note")));
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[1].Id, _versionId), Times.Once);
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[0].Id, groceriesVersion), Times.Once);
    }

    /// <summary>
    /// "Dinners out" may well divide by the bill, which no line can: the lines start on Even.
    /// </summary>
    [Fact]
    public void Under_a_category_that_divides_by_the_bill_lines_start_on_even()
    {
        var bill = Bill(Line("Bacalhau", 22, null, null));
        AnswerRuleChangesOn(bill);

        // A rule the lines are never offered, the way the bill rule is not.
        var page = OpenUnder(bill, Guid.NewGuid());

        page.WaitForAssertion(() => Assert.Contains("even, by default", page.Markup));
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[0].Id, _versionId), Times.Once);
    }

    /// <summary>A category rule naming somebody who has left is passed over for Even.</summary>
    [Fact]
    public void A_category_rule_naming_somebody_who_left_starts_lines_on_even()
    {
        var stays = new UserInfo(Guid.NewGuid(), "Ana", "Ruiz", "ana@example.com");
        var (groceries, groceriesVersion) = AGroceriesRule(new SharesSplitRuleDto
            { Shares = new() { [stays.Id] = 1, [Guid.NewGuid()] = 1 } });
        var bill = Bill(Line("Bananas", 3, null, null));
        AnswerRuleChangesOn(bill);

        var page = OpenUnder(bill, groceries, [stays]);

        page.WaitForAssertion(() => Assert.Contains("Every line has a rule", page.Markup));
        _bills.Verify(b => b.SetRuleAsync(_expense, bill.Items[0].Id, _versionId), Times.Once);
        _bills.Verify(b => b.SetRuleAsync(_expense, It.IsAny<Guid>(), groceriesVersion), Times.Never);
    }
}

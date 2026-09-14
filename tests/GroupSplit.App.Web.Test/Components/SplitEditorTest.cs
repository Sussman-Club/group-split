using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components.Web;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The split step: which of the two ways to divide is chosen, and who is listed.
/// </summary>
/// <remarks>
/// Both halves of this were reported as confusing and both are about the same thing -- how
/// many times a person appears. Splitting by hand used to draw every member twice, once as
/// a field and once in a read-only echo of what had just been typed into it; and the choice
/// between the two ways was a switch whose label changed with its state, so it said neither
/// what was true nor what clicking would do.
/// </remarks>
public class SplitEditorTest : ComponentTest
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Omar = Guid.NewGuid();

    private static readonly UserInfo[] Members =
    [
        new(Ana, "Ana", "Benitez", "ana@example.com"),
        new(Omar, "Omar", "Diaz", "omar@example.com")
    ];

    private static SplitPreviewResponse Divided(string? rule, DateTimeOffset? superseded = null) =>
        new(
        [
            new TransactionSplitResponse(Ana, "Ana Benitez", 60m),
            new TransactionSplitResponse(Omar, "Omar Diaz", 40m)
        ], rule, superseded);

    private IRenderedComponent<SplitEditor> Render(
        IReadOnlyList<SplitInput>? value = null,
        SplitPreviewResponse? preview = null,
        Action<IReadOnlyList<SplitInput>?>? onChanged = null) =>
        Render<SplitEditor>(parameters => parameters
            .Add(editor => editor.Members, Members)
            .Add(editor => editor.Amount, 100m)
            .Add(editor => editor.PayerId, Ana)
            .Add(editor => editor.Value, value)
            .Add(editor => editor.Preview, preview)
            .Add(editor => editor.ValueChanged, splits => onChanged?.Invoke(splits)));

    /// <summary>
    /// Both ways are named and one of them is lit, rather than one label that flips.
    /// </summary>
    [Fact]
    public void The_two_ways_to_divide_are_both_named()
    {
        var editor = Render(preview: Divided("Rent"));

        Assert.Contains("Automatically", editor.Markup, StringComparison.Ordinal);
        Assert.Contains("By hand", editor.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Automatically: the API's answer, once per person, with a line saying what decided it.
    /// </summary>
    [Fact]
    public void Dividing_automatically_lists_each_person_once_with_what_they_owe()
    {
        var editor = Render(preview: Divided("Rent"));

        // Rows, not occurrences in the markup: an avatar carries the name in a title
        // attribute as well as in its text, so counting the string would count the tooltip.
        var rows = editor.FindAll(".gs-row");

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Count(row => row.TextContent.Contains("Ana Benitez", StringComparison.Ordinal)));
        Assert.Equal(1, rows.Count(row => row.TextContent.Contains("Omar Diaz", StringComparison.Ordinal)));

        Assert.Contains("Divided by the \"Rent\" rule.", editor.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Dividing_automatically_with_no_rule_says_what_that_means()
    {
        var editor = Render(preview: Divided(rule: null));

        Assert.Contains("Divided evenly between everyone in the group.", editor.Markup,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression this component was reshaped for: one field per person and no second
    /// list repeating them.
    /// </summary>
    /// <remarks>
    /// A preview is supplied deliberately. By hand the API echoes the stated shares back,
    /// so a component that rendered the preview regardless would draw everybody twice --
    /// which is exactly what the dialog used to do underneath it.
    /// </remarks>
    [Fact]
    public void Dividing_by_hand_lists_each_person_once_as_a_field()
    {
        var stated = new List<SplitInput>
        {
            new() { UserId = Ana, Amount = 60m },
            new() { UserId = Omar, Amount = 40m }
        };

        var editor = Render(value: stated, preview: Divided("Rent"));

        // One field each, and no read-only row anywhere: the fields are the answer, so a
        // list under them would be the same two people a second time.
        Assert.Equal(2, editor.FindAll("input[type=text]").Count);
        Assert.Empty(editor.FindAll(".gs-row"));
    }

    /// <summary>
    /// Stated shares are the person's own arithmetic, so the one thing shown about them is
    /// whether it adds up -- not a rule that had nothing to do with them.
    /// </summary>
    [Fact]
    public void Dividing_by_hand_says_what_is_left_to_assign_and_names_no_rule()
    {
        var short_ = new List<SplitInput>
        {
            new() { UserId = Ana, Amount = 60m },
            new() { UserId = Omar, Amount = 10m }
        };

        var editor = Render(value: short_, preview: Divided("Rent"));

        Assert.Contains("still to assign", editor.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Divided by the", editor.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Choosing "by hand" hands back a division that already adds up, so nobody starts from
    /// a warning they did not cause.
    /// </summary>
    [Fact]
    public async Task Choosing_by_hand_starts_from_a_division_that_adds_up()
    {
        IReadOnlyList<SplitInput>? handed = null;

        var editor = Render(preview: Divided("Rent"), onChanged: splits => handed = splits);

        await ByHand(editor);

        Assert.NotNull(handed);
        Assert.Equal(100m, handed!.Sum(split => split.Amount));
    }

    /// <summary>
    /// And going back hands null, which is not "no shares" but "don't send any" -- the
    /// instruction that leaves the division to the category.
    /// </summary>
    [Fact]
    public async Task Going_back_to_automatic_hands_back_nothing_to_send()
    {
        var stated = new List<SplitInput> { new() { UserId = Ana, Amount = 100m } };

        var handed = new List<IReadOnlyList<SplitInput>?>();

        var editor = Render(value: stated, preview: Divided("Rent"), onChanged: handed.Add);

        await Automatically(editor);

        Assert.Null(Assert.Single(handed));
    }

    /// <summary>
    /// The payoff of versioned rules, said where somebody would otherwise be confused: the
    /// numbers do not match the rule they can see because the rule has changed since.
    /// </summary>
    [Fact]
    public void A_rule_that_has_changed_since_says_so()
    {
        var editor = Render(preview: Divided("Rent", DateTimeOffset.UtcNow.AddDays(-3)));

        Assert.Contains("The rule has changed since this was recorded", editor.Markup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_group_there_is_nobody_to_divide_between()
    {
        var editor = Render<SplitEditor>(parameters => parameters
            .Add(editor => editor.Members, [])
            .Add(editor => editor.Amount, 100m));

        Assert.Contains("Pick a group first", editor.Markup, StringComparison.Ordinal);
    }

    // ---- the bill as one of the answers -------------------------------------------------

    private static ReceiptResponse Bill() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 100m, 0, 0, 100m, 0, true, true,
            [new ReceiptItemResponse(Guid.NewGuid(), "Bacalhau", 100m, 1, 100m, 0, null, null, null)]);

    /// <summary>
    /// The option is absent where an expense cannot have a bill at all -- a create dialog has
    /// nowhere to put one, and a permanently dead option is worse than none.
    /// </summary>
    [Fact]
    public void Creating_an_expense_is_not_offered_the_bill()
    {
        var editor = Render(preview: Divided(null));

        Assert.DoesNotContain("By its bill", editor.Markup, StringComparison.Ordinal);
        Assert.Equal(3, editor.FindAll(".gs-seg-btn").Count);
    }

    /// <summary>
    /// Drawn from the first render on an edit, and disabled until the bill read lands. An
    /// option that appears late moves every option beside it, which is what the control was
    /// built with a fixed width to avoid.
    /// </summary>
    [Fact]
    public void An_edit_draws_the_bill_option_before_the_bill_has_arrived()
    {
        var editor = Render<SplitEditor>(parameters => parameters
            .Add(e => e.Members, Members)
            .Add(e => e.Amount, 100m)
            .Add(e => e.ShowByBill, true)
            .Add(e => e.Preview, Divided(null)));

        var bill = editor.FindAll(".gs-seg-btn")
            .First(element => element.TextContent.Contains("By its bill", StringComparison.Ordinal));

        Assert.Equal(4, editor.FindAll(".gs-seg-btn").Count);
        Assert.True(bill.HasAttribute("disabled"));
    }

    /// <summary>
    /// Choosing it names the group's "divide it by the bill" rule and nothing else: it is a
    /// rule like the two beside it, and the expense divides by it when it is saved.
    /// </summary>
    /// <remarks>
    /// From a bill that is <em>not</em> already dividing the expense, which is the only way
    /// this is a choice at all -- one that already divides by its bill opens on this option,
    /// so pressing it is asking for the state it is in.
    /// </remarks>
    [Fact]
    public async Task Choosing_the_bill_names_the_rule_that_divides_by_it()
    {
        var rule = Guid.NewGuid();
        Guid? named = null;

        var editor = Render<SplitEditor>(parameters => parameters
            .Add(e => e.Members, Members)
            .Add(e => e.Amount, 100m)
            .Add(e => e.ShowByBill, true)
            .Add(e => e.Bill, Bill() with { DividesItsExpense = false })
            .Add(e => e.BillRuleId, rule)
            .Add(e => e.Preview, Divided(null))
            .Add(e => e.SplitRuleIdChanged, id => named = id));

        await Choose(editor, "By its bill");

        Assert.Equal(rule, named);
    }

    /// <summary>
    /// An expense divided by its bill opens on the bill, even though it names no rule.
    /// </summary>
    /// <remarks>
    /// Which is nearly all of them: the division comes from the category -- "Takeaway" points
    /// at the group's bill rule -- so the expense itself names nothing. Reading only the named
    /// rule opened all 38 of the seeded ones on "Automatically", over a receipt the screen
    /// could have been showing.
    /// </remarks>
    [Fact]
    public void An_expense_its_category_divides_by_the_bill_opens_on_the_bill()
    {
        var editor = Render<SplitEditor>(parameters => parameters
            .Add(e => e.Members, Members)
            .Add(e => e.Amount, 100m)
            .Add(e => e.ShowByBill, true)
            .Add(e => e.Bill, Bill())
            .Add(e => e.BillRuleId, Guid.NewGuid())
            // Names nothing of its own, the way an expense filed under a category does not.
            .Add(e => e.SplitRuleId, (Guid?)null)
            .Add(e => e.Preview, Divided("Divide by the bill")));

        var bill = editor.FindAll(".gs-seg-btn")
            .First(element => element.TextContent.Contains("By its bill", StringComparison.Ordinal));

        Assert.Contains("active", bill.ClassName ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// And one whose category divides some other way still opens on Automatically, bill or no
    /// bill: a receipt can be attached to an expense nothing reads it for.
    /// </summary>
    [Fact]
    public void A_bill_that_divides_nothing_leaves_the_choice_on_automatically()
    {
        var editor = Render<SplitEditor>(parameters => parameters
            .Add(e => e.Members, Members)
            .Add(e => e.Amount, 100m)
            .Add(e => e.ShowByBill, true)
            .Add(e => e.Bill, Bill() with { DividesItsExpense = false })
            .Add(e => e.BillRuleId, Guid.NewGuid())
            .Add(e => e.Preview, Divided(null)));

        var chosen = editor.FindAll(".gs-seg-btn")
            .First(element => element.ClassName?.Contains("active", StringComparison.Ordinal) == true);

        Assert.Contains("Automatically", chosen.TextContent, StringComparison.Ordinal);
    }

    private static Task ByHand(IRenderedComponent<SplitEditor> editor) => Choose(editor, "By hand");

    private static Task Automatically(IRenderedComponent<SplitEditor> editor) =>
        Choose(editor, "Automatically");

    /// <summary>
    /// Clicks one of the two options, found by the text somebody would read.
    /// </summary>
    private static async Task Choose(IRenderedComponent<SplitEditor> editor, string option)
    {
        var item = editor.FindAll(".gs-seg-btn")
            .First(element => element.TextContent.Contains(option, StringComparison.Ordinal));

        await item.ClickAsync(new MouseEventArgs());
    }
}

using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The row of chips the create, edit and bank-filing dialogs share: where an expense goes
/// and when it was spent, picked rather than typed.
/// </summary>
/// <remarks>
/// It was only ever tested through those three dialogs, each of which drives it with values
/// of its own, so nothing pinned what the control itself promises them: that Personal goes
/// out as no group, that a select emptying itself is not a pick, and that a day picked by
/// somebody far from Greenwich comes back as the day they clicked. The last two are the ones
/// a dialog cannot paper over -- a stray null moves an expense out of its group, and a day
/// shifted by a zone lands it in the wrong month (#252).
/// </remarks>
public class ExpenseDecisionsTest : ComponentTest
{
    private static readonly ExpenseDecisions.GroupChoice Flat = new(Guid.NewGuid(), "Flat");
    private static readonly ExpenseDecisions.GroupChoice Trip = new(Guid.NewGuid(), "Trip");

    private static readonly DateTime EndOfSeptember = new(2026, 9, 30);

    private readonly List<Guid?> _groupsPicked = [];
    private readonly List<DateTime?> _daysPicked = [];

    private IRenderedComponent<MudPopoverProvider> _popovers = null!;

    /// <summary>
    /// The control as the edit dialog holds it -- in a group, on a day -- with every pick
    /// recorded so a test can say what was reported and how often. Flat on 30 September
    /// unless the test says otherwise.
    /// </summary>
    private IRenderedComponent<ExpenseDecisions> RenderDecisions(
        Action<ComponentParameterCollectionBuilder<ExpenseDecisions>>? caller = null) =>
        RenderDecisions(Flat.Id, EndOfSeptember, caller);

    private IRenderedComponent<ExpenseDecisions> RenderDecisions(Guid? groupId, DateTime day,
        Action<ComponentParameterCollectionBuilder<ExpenseDecisions>>? caller = null)
    {
        _popovers = Render<MudPopoverProvider>();

        return Render<ExpenseDecisions>(parameters =>
        {
            parameters
                .Add(decisions => decisions.Groups, [Flat, Trip])
                .Add(decisions => decisions.GroupId, groupId)
                .Add(decisions => decisions.GroupIdChanged, (Guid? id) => _groupsPicked.Add(id))
                .Add(decisions => decisions.Date, day)
                .Add(decisions => decisions.DateChanged, (DateTime? picked) => _daysPicked.Add(picked));

            caller?.Invoke(parameters);
        });
    }

    private static IElement Chip(IRenderedComponent<ExpenseDecisions> decisions, string label) =>
        decisions.FindAll("button.gs-expense-chip").Single(chip =>
        {
            var name = chip.GetAttribute("aria-label")!;

            return name.StartsWith($"{label}:", StringComparison.Ordinal)
                   || name == $"Add {label}";
        });

    private static IReadOnlyList<string> ChipLabels(IRenderedComponent<ExpenseDecisions> decisions) =>
        [.. decisions.FindAll("button.gs-expense-chip").Select(chip => chip.GetAttribute("aria-label")!)];

    private static IRenderedComponent<MudSelect<Guid?>> GroupSelect(IRenderedComponent<ExpenseDecisions> decisions) =>
        decisions.FindComponents<MudSelect<Guid?>>().Single(select => select.Instance.Label == "Group");

    /// <summary>What the group select's box reads, which is what the person sees in it.</summary>
    private static string? GroupBox(IRenderedComponent<ExpenseDecisions> decisions) =>
        decisions.Find(".gs-picker input").GetAttribute("value");

    private async Task<IRenderedComponent<MudSelect<Guid?>>> OpenGroupsAsync(
        IRenderedComponent<ExpenseDecisions> decisions)
    {
        await Chip(decisions, "group").ClickAsync(new MouseEventArgs());

        var select = GroupSelect(decisions);
        await decisions.InvokeAsync(() => select.Instance.OpenMenu());

        return select;
    }

    /// <summary>Picks an item the way a person does: from the open list, by what it says.</summary>
    private async Task PickAsync(string item)
    {
        var option = _popovers.WaitForElements(".mud-list-item")
            .Single(candidate => candidate.TextContent.Trim() == item);

        await option.ClickAsync(new MouseEventArgs());
    }

    // ---- what the chips say ------------------------------------------------------------------

    [Fact]
    public void The_group_chip_says_the_group_the_caller_named()
    {
        var decisions = RenderDecisions(parameters => parameters.Add(d => d.GroupName, "Flat"));

        var chip = Chip(decisions, "group");

        Assert.Equal("group: Flat. Change it", chip.GetAttribute("aria-label"));
        Assert.Equal("in", chip.QuerySelector(".k")!.TextContent.Trim());
        Assert.Equal("Flat", chip.QuerySelector(".v")!.TextContent.Trim());
        Assert.DoesNotContain("is-empty", chip.ClassName!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The day is worded by the caller, because only the caller's clock knows what "today"
    /// is for the person reading; the chip prints it as given.
    /// </summary>
    [Fact]
    public void The_when_chip_says_the_day_as_the_caller_worded_it()
    {
        var decisions = RenderDecisions(parameters => parameters.Add(d => d.WhenSpent, "Yesterday"));

        var chip = Chip(decisions, "when");

        Assert.Equal("when: Yesterday. Change it", chip.GetAttribute("aria-label"));
        Assert.Equal("Yesterday", chip.QuerySelector(".v")!.TextContent.Trim());
    }

    /// <summary>
    /// A chip with nothing in it offers itself rather than printing a placeholder value: an
    /// expense with no category is "Add category", not "for uncategorized".
    /// </summary>
    [Fact]
    public void A_category_not_yet_chosen_is_offered_rather_than_shown()
    {
        var groceries = new ExpenseDecisions.CategoryChoice(Guid.NewGuid(), "Groceries", "Household 3-way");

        var unset = RenderDecisions(parameters => parameters
            .Add(d => d.ShowCategory, true)
            .Add(d => d.Categories, [groceries]));

        var chip = Chip(unset, "category");

        Assert.Equal("Add category", chip.GetAttribute("aria-label"));
        Assert.Contains("is-empty", chip.ClassName!, StringComparison.Ordinal);

        unset.Render(parameters => parameters.Add(d => d.CategoryId, groceries.Id));

        Assert.Equal("category: Groceries. Change it", Chip(unset, "category").GetAttribute("aria-label"));
    }

    /// <summary>
    /// Filing a bank row asks for two things and no more: the day, the amount and the shop
    /// are the bank's account and not open to an opinion there.
    /// </summary>
    [Fact]
    public void Only_the_chips_the_caller_asks_for_are_drawn()
    {
        var decisions = RenderDecisions(parameters => parameters
            .Add(d => d.GroupName, "Flat")
            .Add(d => d.ShowCategory, true)
            .Add(d => d.ShowPayer, false)
            .Add(d => d.ShowWhen, false)
            .Add(d => d.ShowWhere, false));

        Assert.Equal(["group: Flat. Change it", "Add category"], ChipLabels(decisions));
    }

    // ---- which picker is open ----------------------------------------------------------------

    /// <summary>
    /// One picker at a time, under the row, so the row stays a row and the open chip is the
    /// one whose control is showing.
    /// </summary>
    [Fact]
    public async Task A_chip_opens_its_picker_and_another_chip_swaps_it_for_its_own()
    {
        var decisions = RenderDecisions();

        Assert.Empty(decisions.FindAll(".gs-picker"));

        await Chip(decisions, "group").ClickAsync(new MouseEventArgs());

        Assert.Equal("true", Chip(decisions, "group").GetAttribute("aria-expanded"));
        Assert.Single(decisions.FindComponents<MudSelect<Guid?>>());
        Assert.Empty(decisions.FindComponents<MudDatePicker>());

        await Chip(decisions, "when").ClickAsync(new MouseEventArgs());

        Assert.Equal("false", Chip(decisions, "group").GetAttribute("aria-expanded"));
        Assert.Equal("true", Chip(decisions, "when").GetAttribute("aria-expanded"));
        Assert.Empty(decisions.FindComponents<MudSelect<Guid?>>());
        Assert.Single(decisions.FindComponents<MudDatePicker>());
    }

    [Fact]
    public async Task Pressing_the_open_chip_again_folds_its_picker()
    {
        var decisions = RenderDecisions();

        await Chip(decisions, "group").ClickAsync(new MouseEventArgs());
        await Chip(decisions, "group").ClickAsync(new MouseEventArgs());

        Assert.Empty(decisions.FindAll(".gs-picker"));
        Assert.Equal("false", Chip(decisions, "group").GetAttribute("aria-expanded"));
    }

    /// <summary>
    /// The edit dialog closes the row when moving the expense to another group retires the
    /// payer and category it was showing; a picker left open would offer the old group's
    /// people.
    /// </summary>
    [Fact]
    public async Task A_caller_can_fold_whatever_is_open()
    {
        var decisions = RenderDecisions();

        await Chip(decisions, "when").ClickAsync(new MouseEventArgs());
        await decisions.InvokeAsync(decisions.Instance.Close);

        Assert.Empty(decisions.FindAll(".gs-picker"));
    }

    // ---- the group ---------------------------------------------------------------------------

    [Fact]
    public async Task Picking_a_group_reports_its_id()
    {
        var decisions = RenderDecisions();

        await OpenGroupsAsync(decisions);
        await PickAsync("Trip");

        Assert.Equal([Trip.Id], _groupsPicked);
    }

    /// <summary>
    /// Personal leaves the select as no group, which is what the API and every caller mean
    /// by it. The value it carries inside the select is never seen outside it.
    /// </summary>
    [Fact]
    public async Task Picking_Personal_reports_no_group()
    {
        var decisions = RenderDecisions();

        await OpenGroupsAsync(decisions);
        await PickAsync("Personal (just you)");

        Assert.Equal([null], _groupsPicked);
    }

    /// <summary>
    /// The box names the group it holds. Without a display function it showed the raw id,
    /// which is on screen exactly while somebody is reading it.
    /// </summary>
    [Fact]
    public async Task The_open_picker_shows_the_group_by_name_and_Personal_as_Personal()
    {
        var decisions = RenderDecisions(Trip.Id, EndOfSeptember);

        await Chip(decisions, "group").ClickAsync(new MouseEventArgs());

        Assert.Equal("Trip", GroupBox(decisions));

        decisions.Render(parameters => parameters.Add(d => d.GroupId, (Guid?)null));

        Assert.Equal("Personal (just you)", GroupBox(decisions));
    }

    /// <summary>
    /// The bank-filing case, before anybody has said where the row goes. The select opened
    /// already holding Personal, so choosing Personal raised nothing and the dialog stayed on
    /// "Pick a group" with Save disabled and no way to enable it.
    /// </summary>
    [Fact]
    public async Task Before_a_group_is_chosen_the_picker_holds_nothing_and_Personal_can_still_be_picked()
    {
        var decisions = RenderDecisions(null, EndOfSeptember, parameters => parameters
            .Add(d => d.GroupChosen, false)
            .Add(d => d.GroupName, "Pick a group"));

        var select = await OpenGroupsAsync(decisions);

        Assert.Null(select.Instance.Value);
        Assert.Equal("", GroupBox(decisions) ?? "");

        await PickAsync("Personal (just you)");

        Assert.Equal([null], _groupsPicked);
    }

    /// <summary>
    /// Where the expense is on its way into a group by definition, "just you" is not one of
    /// the answers.
    /// </summary>
    [Fact]
    public async Task Without_Personal_allowed_only_the_groups_are_offered()
    {
        var decisions = RenderDecisions(parameters => parameters.Add(d => d.AllowPersonal, false));

        await OpenGroupsAsync(decisions);

        var offered = _popovers.WaitForElements(".mud-list-item")
            .Select(item => item.TextContent.Trim())
            .ToArray();

        Assert.Equal(["Flat", "Trip"], offered);
    }

    /// <summary>
    /// No item carries a null, so a null out of the select is the control reporting that it
    /// holds nothing -- the state the caller left it in. Passing it on would move an expense
    /// to Personal that nobody moved.
    /// </summary>
    [Fact]
    public async Task The_select_emptying_itself_is_not_taken_for_a_pick()
    {
        var decisions = RenderDecisions();

        await Chip(decisions, "group").ClickAsync(new MouseEventArgs());

        var select = GroupSelect(decisions);
        await decisions.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(null));

        Assert.Empty(_groupsPicked);
    }

    // ---- the day -----------------------------------------------------------------------------

    /// <summary>
    /// A day picked by somebody far from Greenwich is the day they clicked, and is stored as
    /// that day's midnight UTC -- which reads back as the same day wherever they are (#252).
    /// </summary>
    /// <remarks>
    /// The control reports the calendar's day as it is, with no time and no zone, and the
    /// caller's clock turns it into the instant. Here the clock is the edit dialog's, editing
    /// a bank row: a date with no time. From UTC-5 a midnight-UTC date is the previous
    /// evening, and from UTC+12 the same day's noon, so any conversion on the way through
    /// would move the expense a day.
    /// </remarks>
    [Theory]
    [InlineData(-5)]
    [InlineData(12)]
    public async Task A_day_picked_far_from_utc_is_reported_and_stored_as_that_day(int hours)
    {
        var clock = ReaderAt(TimeSpan.FromHours(hours));

        var posted = TransactionDay.Stored(new DateOnly(2026, 9, 30));
        var (opensOn, _) = clock.Pickers(posted);

        var decisions = RenderDecisions(Flat.Id, opensOn);

        await Chip(decisions, "when").ClickAsync(new MouseEventArgs());

        var picker = decisions.FindComponents<MudDatePicker>().Single(field => field.Instance.Label == "Date");

        Assert.Equal(EndOfSeptember, picker.Instance.Date);

        await decisions.InvokeAsync(picker.Instance.OpenAsync);

        await _popovers.WaitForElements("button.mud-picker-calendar-day")
            .Single(day => day.TextContent.Trim() == "12" && !day.ClassList.Contains("mud-hidden"))
            .ClickAsync(new MouseEventArgs());

        var picked = Assert.Single(_daysPicked);

        Assert.Equal(new DateTime(2026, 9, 12), picked);
        Assert.Equal(TimeSpan.Zero, picked!.Value.TimeOfDay);

        var stored = clock.Edited(posted, picked, null);

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), stored);
        Assert.Equal(new DateOnly(2026, 9, 12), clock.Day(stored));
    }

    /// <summary>
    /// The picker opens on the day the caller holds, not on the machine's today. Through the
    /// clock, a date stored as midnight UTC is that same day for both readers.
    /// </summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(12)]
    public async Task The_date_picker_opens_on_the_day_the_caller_holds(int hours)
    {
        var clock = ReaderAt(TimeSpan.FromHours(hours));
        var (opensOn, time) = clock.Pickers(TransactionDay.Stored(new DateOnly(2026, 10, 1)));

        var decisions = RenderDecisions(Flat.Id, opensOn, parameters => parameters.Add(d => d.Time, time));

        await Chip(decisions, "when").ClickAsync(new MouseEventArgs());

        Assert.Equal(new DateTime(2026, 10, 1),
            decisions.FindComponents<MudDatePicker>().Single(field => field.Instance.Label == "Date").Instance.Date);
        Assert.Null(decisions.FindComponent<MudTimePicker>().Instance.Time);
    }
}

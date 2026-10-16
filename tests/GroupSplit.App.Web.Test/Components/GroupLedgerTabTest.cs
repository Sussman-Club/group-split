using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group's Ledger tab: one list where there were two, with two figures over it that
/// count expenses whatever the list is filtered to.
/// </summary>
/// <remarks>
/// It replaces the Expenses and Activity tabs and inherits both of their contracts. From
/// Expenses: the cards and the list can never describe different questions, which is the
/// defect they were written against (#174) -- picking a range emptied the list and left the
/// cards showing the all-time figures under a caption naming the range. From Activity: both
/// settlements and expenses can be edited and deleted.
/// <para>
/// And one contract that exists only because of the merge: the kind chips must not move the
/// figures. Total spent counts expenses whatever is on screen -- that label is the only
/// thing keeping a settlement out of a spending total -- so filtering to settlements must
/// not send a summary request at all.
/// </para>
/// </remarks>
public class GroupLedgerTabTest : ComponentTest
{
    private static readonly Guid Group = Guid.NewGuid();

    private readonly Mock<IGroupsClient> _groups = new();
    private readonly Mock<ITransactionsClient> _transactions = new();
    private readonly Mock<ITransactionCommands> _commands = new();
    private readonly Mock<IDialogService> _dialogs = new();

    /// <summary>Every summary request the component made, in order, as it asked it.</summary>
    private readonly List<Ask> _asks = [];

    /// <summary>The kind each ledger request asked for, in order.</summary>
    private readonly List<int?> _kinds = [];

    private Func<Ask, Task<TransactionSummaryResponse>> _answer =
        _ => Task.FromResult(new TransactionSummaryResponse(5, 29m));

    private List<GroupLedgerEntryResponse> _entries = [];
    private IReadOnlyList<CategorySpendingResponse> _categories = [];

    /// <summary>
    /// When set, the next read of the rows waits on it -- a reload still in flight while the
    /// test does something else.
    /// </summary>
    private TaskCompletionSource? _holdNextRead;

    /// <summary>What the confirmation dialog comes back with. Yes, unless a test says otherwise.</summary>
    private DialogResult _confirmation = DialogResult.Ok(true);

    private record Ask(DateTimeOffset? From, DateTimeOffset? To, string? Search)
    {
        public bool IsAllTime => From is null && To is null;
    }

    public GroupLedgerTabTest()
    {
        _groups
            .Setup(client => client.GetGroupTransactionsSummaryAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid _, DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, bool? _,
                string search, string _, CancellationToken _) =>
            {
                var ask = new Ask(from, to, search);
                _asks.Add(ask);

                return _answer(ask);
            });

        _groups
            .Setup(client => client.GetGroupSpendingByCategoryAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
                It.IsAny<Guid?>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CategorySpendingSummaryResponse(_categories));

        _groups
            .Setup(client => client.GetGroupActivityAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool?>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, DateTimeOffset? _, DateTimeOffset? _, int? kind, string _, Guid? _,
                bool? _, Guid? _, string _, bool? _, int? _, int? _, CancellationToken _) =>
            {
                _kinds.Add(kind);

                // What the server holds at the moment it is asked, which is not necessarily
                // what it holds by the time the answer arrives.
                var rows = _entries.ToList();

                if (_holdNextRead is { } held)
                {
                    _holdNextRead = null;
                    await held.Task;
                }

                return new PagedResponseOfGroupLedgerEntryResponse(rows, 1, 10, rows.Count);
            });

        // The caller's own share of the same set: the second line under the total. Not what
        // these are about, so it answers the same thing every time.
        _transactions
            .Setup(client => client.GetTransactionSharesSummaryAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExpenseShareSummaryResponse(5, 29m, 9m, 9m));

        // A real MudBlazor reference, already finished with the answer -- the state a
        // component is actually handed. See ConfirmationResultTest for why a stand-in is
        // the wrong tool here.
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<ConfirmationDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ConfirmationDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);

                reference.Dismiss(_confirmation);

                return reference;
            });

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_commands.Object);
        Services.AddSingleton(_dialogs.Object);
        Services.AddSingleton(Mock.Of<ITransactionsPageStateService>());
    }

    private IRenderedComponent<GroupLedgerTab> Render() =>
        base.Render<GroupLedgerTab>(parameters => parameters.Add(tab => tab.GroupId, Group));

    /// <summary>The big number on the first card, and the money on the second.</summary>
    private static (string Count, string Total) Cards(IRenderedComponent<GroupLedgerTab> tab)
    {
        var values = tab.FindAll(".gs-stat-value");

        return (values[0].TextContent.Trim(), values[1].TextContent.Trim());
    }

    private static Task ClickChipAsync(IRenderedComponent<GroupLedgerTab> tab, string label) =>
        tab.FindAll(".gs-chip button, button.gs-chip")
            .First(chip => chip.TextContent.Contains(label)).ClickAsync(new());

    /// <summary>
    /// Sets the span, through the filter component's own callback -- the presets live in a
    /// Mud popover, outside the tree bUnit renders. See the note on the inbox's equivalent.
    /// </summary>
    private static Task PickSpanAsync(IRenderedComponent<GroupLedgerTab> tab, DateFilterPreset preset)
    {
        var filter = tab.FindComponent<DateRangeFilter>();

        return tab.InvokeAsync(() => filter.Instance.ValueChanged.InvokeAsync(new DateFilter(preset)));
    }

    private static IReadOnlyList<IElement> Buttons(IRenderedComponent<GroupLedgerTab> tab, string prefix) =>
        tab.FindAll("button[aria-label^='" + prefix + "']");

    private static CategorySpendingResponse Category(string name, int count, decimal total, decimal share = 0m) =>
        new(Guid.NewGuid(), name, count, total, share);

    private static GroupLedgerEntryResponse Transfer(Guid id, string from = "Anabel", string to = "Daniel") =>
        new()
        {
            Id = id,
            Kind = ActivityKind.Transfer,
            Name = "Settlement",
            Amount = 3.60m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = Guid.NewGuid(),
            PaidByUserName = from,
            PaidToUserId = Guid.NewGuid(),
            PaidToUserName = to
        };

    private static GroupLedgerEntryResponse Expense(Guid id, string name = "Dinner", decimal? share = 12m) =>
        new()
        {
            Id = id,
            Kind = ActivityKind.Expense,
            Name = name,
            Amount = 40m,
            DateTime = DateTimeOffset.UtcNow,
            PaidByUserId = Guid.NewGuid(),
            PaidByUserName = "Omar",
            Share = share,
            RunningBalance = -12m
        };

    /// <summary>An expense filed from a bank row: it knows the shop, and the shop has a mark.</summary>
    private static GroupLedgerEntryResponse AtAShop(Guid id) =>
        Expense(id, "Weekly shop") with
        {
            MerchantName = "Lidl",
            MerchantLogoUrl = "/_content/GroupSplit.App.Shared/merchants/lidl.svg"
        };

    // ---- the figures over the list ------------------------------------------------------

    [Fact]
    public async Task A_long_category_breakdown_stays_compact_until_somebody_asks_to_see_it()
    {
        _categories =
        [
            Category("Household", 5, 275.14m, 68.78m),
            Category("Utilities", 1, 61m, 32.90m),
            Category("Groceries", 1, 30m, 0.63m),
            Category("Dinners out", 1, 20m, 20m),
            Category("Travel", 2, 15m, 7.50m),
            Category("Subscriptions", 1, 8m, 4m)
        ];

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Contains("Household", tab.Find(".gs-category-summary").TextContent));
        Assert.Equal(6, tab.FindAll(".gs-category-chips .gs-category-leader").Count);
        Assert.Contains("$275.14", tab.Find(".gs-category-summary").TextContent);
        var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);
        reference.Dismiss(DialogResult.Ok(_categories[4]));
        _dialogs.Setup(dialogs => dialogs.ShowAsync<CategorySpendingDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<CategorySpendingDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(reference);

        var showAll = tab.Find("button.gs-category-toggle");
        Assert.Equal("View all categories", showAll.GetAttribute("aria-label"));
        await showAll.ClickAsync(new());

        _dialogs.Verify(dialogs => dialogs.ShowAsync<CategorySpendingDialog>(
            "Spending by category",
            It.Is<DialogParameters<CategorySpendingDialog>>(parameters =>
                parameters.Get<CategorySpendingSummaryResponse>(nameof(CategorySpendingDialog.Spending)).Categories.Count == 6),
            It.IsAny<DialogOptions>()), Times.Once);
        _groups.Verify(client => client.GetGroupSpendingByCategoryAsync(
            Group, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(),
            _categories[4].CategoryId, It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The tab opens on the current month, and asks about that month.
    /// </summary>
    /// <remarks>
    /// It opened on all time, which over a ledger going back years bought nothing but a
    /// "total spent" across a span nobody chose -- the list itself only ever shows the
    /// newest twenty-five either way. The empty state names the month for the same reason
    /// the chip does: a quiet month must not read as an empty group.
    /// </remarks>
    [Fact]
    public void The_cards_start_on_the_current_month()
    {
        var tab = Render();

        Assert.Equal(("5", "$29.00"), Cards(tab));

        var opening = Assert.Single(_asks);

        Assert.False(opening.IsAllTime);
        Assert.NotNull(opening.From);
        Assert.NotNull(opening.To);
        Assert.Contains("this month", tab.Markup);
    }

    /// <summary>
    /// A group that chose All time opens on all of it (#405): a trip from last month is not
    /// an empty month, and an empty ledger there is a group with nothing in it.
    /// </summary>
    [Fact]
    public void A_group_that_opens_on_all_time_asks_about_all_time()
    {
        var tab = base.Render<GroupLedgerTab>(parameters => parameters
            .Add(t => t.GroupId, Group)
            .Add(t => t.DefaultPeriod, ActivityPeriod.AllTime));

        Assert.True(Assert.Single(_asks).IsAllTime);

        var (heading, blurb) = Empty(tab);

        Assert.Equal("Nothing recorded yet", heading);
        Assert.DoesNotContain("All time", blurb);
    }

    /// <summary>
    /// The group's default is the opening span, so it is the one the URL leaves out -- and
    /// This month, picked on a group that opens on all time, is a span somebody chose.
    /// </summary>
    [Fact]
    public async Task On_a_group_that_opens_on_all_time_this_month_is_a_filter()
    {
        var tab = base.Render<GroupLedgerTab>(parameters => parameters
            .Add(t => t.GroupId, Group)
            .Add(t => t.DefaultPeriod, ActivityPeriod.AllTime));

        await PickSpanAsync(tab, DateFilterPreset.ThisMonth);

        var (heading, blurb) = Empty(tab);

        Assert.Equal("Nothing matches", heading);
        Assert.Equal("Nothing in this month.", blurb);
    }

    [Fact]
    public async Task Picking_a_range_asks_about_that_range_and_shows_what_comes_back()
    {
        _answer = ask => Task.FromResult(ask.IsAllTime
            ? new TransactionSummaryResponse(5, 29m)
            : new TransactionSummaryResponse(0, 0m));

        var tab = Render();

        await PickSpanAsync(tab, DateFilterPreset.LastMonth);

        var narrowed = _asks.Last();

        Assert.NotNull(narrowed.From);
        Assert.NotNull(narrowed.To);

        // The whole point of #174: nothing on the cards still describes all time.
        Assert.Equal(("0", "$0.00"), Cards(tab));
        Assert.Contains("last month", tab.Markup);
    }

    /// <summary>
    /// The defect itself, as a race rather than as a wrong request. Every request the tab
    /// makes is correct; the group page re-renders it on each of three announcements about
    /// the group, and the answer from before a chip was clicked could land after the
    /// filtered one -- putting the old figures back on cards captioned with the new range.
    /// </summary>
    [Fact]
    public async Task An_earlier_answer_that_arrives_late_does_not_land_on_the_cards()
    {
        var openingAnswered = new TaskCompletionSource<TransactionSummaryResponse>();

        // Keyed on the *first* ask rather than on all time: the tab opens on the current
        // month now, so "the stale one" is the month's answer and not an all-time one.
        var asked = 0;

        _answer = _ => ++asked == 1
            ? openingAnswered.Task
            : Task.FromResult(new TransactionSummaryResponse(0, 0m));

        var tab = Render();

        // The first read is still in flight, the way a cold start leaves it. The range is
        // picked while it is, which is the ordinary way to use the page.
        await PickSpanAsync(tab, DateFilterPreset.LastMonth);

        Assert.Equal(("0", "$0.00"), Cards(tab));

        // Now the stale one answers. The component resumes on the renderer's dispatcher, so
        // it is given its turns there before anything is looked at -- asserting straight
        // after the result is set would pass by having looked too early to see the damage.
        openingAnswered.SetResult(new TransactionSummaryResponse(5, 29m));

        for (var turn = 0; turn < 5; turn++)
            await tab.InvokeAsync(() => { });

        Assert.Equal(("0", "$0.00"), Cards(tab));
    }

    /// <summary>
    /// The group page subscribes to three of its own state's announcements and re-renders
    /// this tab on each. A request per render is what opened the window above, so a render
    /// that changes no part of the question asks nothing.
    /// </summary>
    [Fact]
    public void Rendering_again_without_a_new_question_asks_nothing_again()
    {
        var tab = Render();

        Assert.Single(_asks);

        tab.Render();
        tab.Render(parameters => parameters.Add(component => component.GroupId, Group));

        Assert.Single(_asks);
    }

    /// <summary>
    /// The counterpart: the narrowing has not moved, but the figures under it have, and the
    /// cards cannot notice that on their own.
    /// </summary>
    [Fact]
    public async Task A_write_announced_anywhere_reads_the_cards_again()
    {
        var tab = Render();

        Assert.Single(_asks);

        _answer = _ => Task.FromResult(new TransactionSummaryResponse(6, 41.50m));

        await Changes.NotifyTransactionsChangedAsync();

        tab.Render();

        Assert.Equal(2, _asks.Count);
        Assert.Equal(("6", "$41.50"), Cards(tab));

        // Re-asked about the span in force, not widened back to the tab's default. A write
        // is news about the figures, not a reason to change the question.
        Assert.Equal(_asks[0].From, _asks.Last().From);
        Assert.Equal(_asks[0].To, _asks.Last().To);
    }

    [Fact]
    public void A_failed_summary_read_is_retried_on_a_later_render()
    {
        var attempts = 0;
        _answer = _ => ++attempts == 1
            ? throw new HttpRequestException("the server could not be reached")
            : Task.FromResult(new TransactionSummaryResponse(6, 41.50m));

        var tab = Render();

        Assert.Single(_asks);
        Assert.Equal(("0", "$0.00"), Cards(tab));

        tab.Render(parameters => parameters.Add(component => component.GroupId, Group));

        Assert.Equal(2, _asks.Count);
        Assert.Equal(("6", "$41.50"), Cards(tab));
    }

    [Fact]
    public async Task An_older_same_range_answer_does_not_replace_one_started_by_a_write()
    {
        var oldAnswer = new TaskCompletionSource<TransactionSummaryResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        _answer = _ => ++attempts == 1
            ? oldAnswer.Task
            : Task.FromResult(new TransactionSummaryResponse(6, 41.50m));

        var tab = Render();

        Assert.Single(_asks);

        await Changes.NotifyTransactionsChangedAsync();

        Assert.Equal(2, _asks.Count);
        Assert.Equal(("6", "$41.50"), Cards(tab));

        oldAnswer.SetResult(new TransactionSummaryResponse(5, 29m));
        for (var turn = 0; turn < 5; turn++)
            await tab.InvokeAsync(() => { });

        Assert.Equal(("6", "$41.50"), Cards(tab));
    }

    [Fact]
    public async Task Searching_narrows_the_cards_as_well_as_the_list()
    {
        _answer = ask => Task.FromResult(ask.Search is null
            ? new TransactionSummaryResponse(5, 29m)
            : new TransactionSummaryResponse(1, 4.50m));

        var tab = Render();

        var search = tab.Find(".gs-search input");
        await search.InputAsync(new() { Value = "coffee" });

        // The box is debounced, so the request follows the pause rather than the keystroke.
        tab.WaitForAssertion(() => Assert.Equal("coffee", _asks.Last().Search), TimeSpan.FromSeconds(5));
        tab.WaitForAssertion(() => Assert.Equal(("1", "$4.50"), Cards(tab)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_refused_read_says_so_instead_of_showing_the_wrong_figures()
    {
        _answer = _ => throw new HttpRequestException("the server could not be reached");

        var tab = Render();

        Assert.Equal(("0", "$0.00"), Cards(tab));

        Snackbar.Verify(
            bar => bar.Add(It.Is<string>(message => message.Contains("Could not load this group's ledger.")),
                Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    // ---- the merge itself ---------------------------------------------------------------

    /// <summary>
    /// The contract that makes one tab safe. Total spent counts expenses whatever the list
    /// is filtered to, so choosing Settlements narrows the list, asks nothing new about the
    /// figures, and leaves them where they were.
    /// </summary>
    [Fact]
    public async Task Filtering_by_kind_moves_the_list_and_leaves_the_total_alone()
    {
        var tab = Render();

        Assert.Single(_asks);
        Assert.Equal(("5", "$29.00"), Cards(tab));

        await ClickChipAsync(tab, "Settlements");

        Assert.Single(_asks);
        Assert.Equal(("5", "$29.00"), Cards(tab));
        Assert.Equal((int)ActivityKind.Transfer, _kinds.Last());

        await ClickChipAsync(tab, "Expenses");

        Assert.Single(_asks);
        Assert.Equal((int)ActivityKind.Expense, _kinds.Last());
    }

    /// <summary>
    /// A kind chosen while the rows are reloading is applied once that reload lands (#323).
    /// </summary>
    /// <remarks>
    /// Only one reload runs at a time, and one asked for meanwhile used to be dropped -- so
    /// the chip said Settlements over the rows of everything, until something else happened
    /// to reload them.
    /// </remarks>
    [Fact]
    public async Task A_kind_chosen_during_a_reload_is_applied_once_it_lands()
    {
        _entries = [Expense(Guid.NewGuid()), Transfer(Guid.NewGuid())];

        var tab = Render();

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holdNextRead = held;

        // A write somewhere reloads the rows, and the read is still out...
        var reloading = tab.InvokeAsync(() => Changes.NotifyTransactionsChangedAsync());

        // ...when Settlements is chosen.
        var choosing = ClickChipAsync(tab, "Settlements");

        held.SetResult();
        await reloading;
        await choosing;

        tab.WaitForAssertion(() => Assert.Equal((int)ActivityKind.Transfer, _kinds.Last()));
    }

    /// <summary>
    /// A row deleted while an earlier delete's reload is still out does not stay on screen
    /// (#323).
    /// </summary>
    /// <remarks>
    /// The second delete's reload was dropped because the first's was running -- and the
    /// first's read had been answered before the second delete reached the server, so it
    /// still had the row in it.
    /// </remarks>
    [Fact]
    public async Task A_second_delete_during_a_reload_does_not_leave_its_row_on_screen()
    {
        var dinner = Expense(Guid.NewGuid(), "Dinner");
        var taxi = Expense(Guid.NewGuid(), "Taxi");
        var lunch = Expense(Guid.NewGuid(), "Lunch");

        _entries = [dinner, taxi, lunch];

        var tab = Render();
        tab.WaitForAssertion(() => Assert.Contains("Taxi", tab.Markup, StringComparison.Ordinal));

        // The first delete lands, and its reload asks before the second one does.
        _entries = [taxi, lunch];

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holdNextRead = held;

        var firstReload = tab.InvokeAsync(() => Changes.NotifyTransactionsChangedAsync());

        // The second delete lands while that read is still out.
        _entries = [lunch];
        var secondReload = tab.InvokeAsync(() => Changes.NotifyTransactionsChangedAsync());

        held.SetResult();
        await firstReload;
        await secondReload;

        tab.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Taxi", tab.Markup, StringComparison.Ordinal);
            Assert.Contains("Lunch", tab.Markup, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Everything is the default, because the question "what has happened here" does not
    /// distinguish -- and it is the reader's default that the two tabs never offered.
    /// </summary>
    [Fact]
    public void Everything_is_the_default_and_asks_for_no_kind_at_all()
    {
        Render();

        Assert.NotEmpty(_kinds);
        Assert.All(_kinds, Assert.Null);
    }

    /// <summary>
    /// Both expenses and settlements offer both edit and delete actions on the ledger.
    /// </summary>
    [Fact]
    public void A_settlement_and_an_expense_both_offer_an_edit_and_a_delete()
    {
        _entries = [Transfer(Guid.NewGuid()), Expense(Guid.NewGuid())];

        var tab = Render();

        // Named after the row rather than "Delete" / "Edit": there is one of these per entry and
        // they are otherwise indistinguishable to a screen reader.
        var deletes = Buttons(tab, "Delete");
        var edits = Buttons(tab, "Edit");

        Assert.Equal(2, deletes.Count);
        Assert.Contains(deletes, button => button.GetAttribute("aria-label")!.Contains("Anabel paid Daniel"));
        Assert.Contains(deletes, button => button.GetAttribute("aria-label")!.Contains("Dinner"));

        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, button => button.GetAttribute("aria-label")!.Contains("Anabel paid Daniel"));
        Assert.Contains(edits, button => button.GetAttribute("aria-label")!.Contains("Dinner"));
    }

    [Fact]
    public async Task Editing_a_settlement_passes_the_settlement_to_the_dialog()
    {
        var id = Guid.NewGuid();
        var transfer = Transfer(id);
        _entries = [transfer];

        DialogParameters? capturedParams = null;
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<UpdateSettlementDialog>(
                It.IsAny<string>(),
                It.IsAny<DialogParameters>(),
                It.IsAny<DialogOptions>()))
            .Callback((string _, DialogParameters parameters, DialogOptions _) =>
            {
                capturedParams = parameters;
            })
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);
                reference.Dismiss(DialogResult.Ok<JsonPatchDocument<UpdateTransactionRequest>?>(null));
                return reference;
            });

        var tab = Render();

        await tab.InvokeAsync(() => Buttons(tab, "Edit").Single().Click());

        Assert.NotNull(capturedParams);
        var original = Assert.IsType<TransactionResponse>(capturedParams[nameof(UpdateSettlementDialog.Original)]);
        Assert.Equal(id, original.Id);
        Assert.Equal(transfer.Name, original.Name);
        Assert.Equal(transfer.Amount, original.Amount);
    }

    [Fact]
    public async Task Editing_an_expense_passes_the_category_and_category_id_to_the_dialog()
    {
        var categoryId = Guid.NewGuid();
        var expense = Expense(Guid.NewGuid(), "Dinner") with
        {
            CategoryId = categoryId,
            Category = "Food"
        };
        _entries = [expense];

        DialogParameters? capturedParams = null;
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<UpdateTransactionDialog>(
                It.IsAny<string>(),
                It.IsAny<DialogParameters>(),
                It.IsAny<DialogOptions>()))
            .Callback((string _, DialogParameters parameters, DialogOptions _) =>
            {
                capturedParams = parameters;
            })
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);
                reference.Dismiss(DialogResult.Ok<JsonPatchDocument<UpdateTransactionRequest>?>(null));
                return reference;
            });

        var tab = Render();

        await tab.InvokeAsync(() => Buttons(tab, "Edit").Single().Click());

        Assert.NotNull(capturedParams);
        var original = Assert.IsType<TransactionResponse>(capturedParams[nameof(UpdateTransactionDialog.Original)]);
        Assert.Equal(categoryId, original.CategoryId);
        Assert.Equal("Food", original.Category);
    }

    [Fact]
    public async Task Confirming_deletes_the_settlement_and_names_it_as_one()
    {
        var id = Guid.NewGuid();
        _entries = [Transfer(id)];

        var tab = Render();

        await tab.InvokeAsync(() => Buttons(tab, "Delete").Single().Click());

        // Named as a settlement so a failure does not report it as an expense that could
        // not be deleted.
        _commands.Verify(
            commands => commands.DeleteAsync(id, "Settlement", "settlement", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Somebody on the settlement has left the group, so the server would refuse to delete
    /// it. The tab says why, with one button, rather than offering a delete that cannot work.
    /// </summary>
    [Fact]
    public async Task A_settlement_with_a_leaver_on_it_is_explained_rather_than_offered_for_deletion()
    {
        var id = Guid.NewGuid();
        _entries = [Transfer(id)];

        var guard = new Mock<IDepartedDeleteGuard>();
        guard.Setup(g => g.FirstLeaverOnAsync(id, It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync("Sofia Kovács");
        Services.AddSingleton(guard.Object);

        var tab = Render();

        await tab.InvokeAsync(() => Buttons(tab, "Delete").Single().Click());

        _dialogs.Verify(dialogs => dialogs.ShowAsync<ConfirmationDialog>(
            "This settlement stays",
            It.Is<DialogParameters<ConfirmationDialog>>(parameters =>
                ((string)parameters[nameof(ConfirmationDialog.ContentText)]!).StartsWith("Sofia Kovács has left the group")
                && false.Equals(parameters[nameof(ConfirmationDialog.ShowCancel)])),
            It.IsAny<DialogOptions>()), Times.Once);

        _commands.Verify(
            commands => commands.DeleteAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Declining_the_confirmation_deletes_nothing()
    {
        _entries = [Transfer(Guid.NewGuid())];
        _confirmation = DialogResult.Ok(false);

        var tab = Render();

        await tab.InvokeAsync(() => Buttons(tab, "Delete").Single().Click());

        _commands.Verify(
            commands => commands.DeleteAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Cancelling, which is not the same as saying no: the dialog carries no result at all.
    /// The listing has to treat it as a no rather than as a yes or an error.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_confirmation_deletes_nothing()
    {
        _entries = [Transfer(Guid.NewGuid())];
        _confirmation = DialogResult.Cancel();

        var tab = Render();

        await tab.InvokeAsync(() => Buttons(tab, "Delete").Single().Click());

        _commands.Verify(
            commands => commands.DeleteAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// What the dinner cost and what it cost the reader are different numbers -- and a
    /// transfer has no share, rather than a share of nothing.
    /// </summary>
    [Fact]
    public void An_expense_carries_a_share_and_a_settlement_carries_a_dash()
    {
        _entries = [Expense(Guid.NewGuid()), Transfer(Guid.NewGuid())];

        var tab = Render();

        Assert.Contains("$12.00", tab.Markup);
        Assert.Contains("—", tab.Markup);
    }

    // ---- where an entry happened --------------------------------------------------------

    /// <summary>
    /// The ledger is the surface this was asked for: an expense filed from a bank leads
    /// with the shop's mark rather than saying where it happened only in words.
    /// </summary>
    /// <remarks>
    /// The shop used to ride the corner of the payer's avatar, at 17px on these rows, which
    /// is below the size any logo is still a logo at. It has the tile now and the payer has
    /// the corner. <see cref="MerchantMarkTest"/> carries the whole of that argument.
    /// </remarks>
    [Fact]
    public void An_expense_filed_from_a_bank_row_leads_with_the_shops_mark()
    {
        _entries = [AtAShop(Guid.NewGuid())];

        var tab = Render();

        var place = tab.Find("img.gs-mark-place");

        Assert.Equal("/_content/GroupSplit.App.Shared/merchants/lidl.svg", place.GetAttribute("src"));
        Assert.Equal("Lidl", place.GetAttribute("title"));

        // The payer is still on the row -- who fronted the money is what a ledger is read
        // for -- on the corner rather than under the shop.
        Assert.Equal("O", tab.Find(".gs-mark-who").TextContent.Trim());
    }

    [Fact]
    public void An_expense_nobody_imported_is_the_payer_and_nothing_else()
    {
        _entries = [Expense(Guid.NewGuid())];

        var tab = Render();

        Assert.Empty(tab.FindAll("img.gs-mark-place"));
        Assert.Empty(tab.FindAll(".gs-mark-who"));
        Assert.Equal("O", tab.Find(".gs-avatar").TextContent.Trim());
    }

    /// <summary>A settlement is one member paying another, so there is no shop to lead with.</summary>
    /// <remarks>
    /// And its mark is a circle. The column's rule is that a square is a place and a circle
    /// is a person, so the one entry with no shop behind it was the one wearing a square --
    /// and, at 40px beside 28px avatars, the biggest thing in the column as well.
    /// </remarks>
    [Fact]
    public void A_settlement_gets_a_persons_mark_rather_than_a_places()
    {
        _entries = [Transfer(Guid.NewGuid())];

        var tab = Render();

        Assert.Empty(tab.FindAll("img.gs-mark-place"));
        Assert.NotEmpty(tab.FindAll(".gs-row-icon.transfer"));
    }

    // ---- what an empty ledger says ------------------------------------------------------

    /// <summary>The empty state's heading and the sentence under it.</summary>
    private static (string Heading, string Blurb) Empty(IRenderedComponent<GroupLedgerTab> tab)
    {
        tab.WaitForElement(".gs-empty > span", TimeSpan.FromSeconds(5));

        return (tab.Find(".gs-empty h3").TextContent.Trim(),
            tab.Find(".gs-empty > span").TextContent.Trim());
    }

    /// <summary>
    /// The month the tab opens on is the tab's doing rather than the reader's, and the
    /// heading says so.
    /// </summary>
    /// <remarks>
    /// The heading turned on IsNarrowed, which counts any span at all -- and since the
    /// range starts at the current month that is true before anybody has touched anything.
    /// So a group created a minute ago opened on "Nothing matches", naming a filter nobody
    /// had set, which is this branch's own subject pointed back at the reader.
    /// </remarks>
    [Fact]
    public void A_ledger_nobody_has_filtered_does_not_say_nothing_matches()
    {
        var tab = Render();

        var (heading, blurb) = Empty(tab);

        Assert.Equal("Nothing recorded yet", heading);
        Assert.Contains("this month", blurb);
        Assert.Contains("All time", blurb);
    }

    /// <summary>
    /// From all time there is nowhere wider to go, so the sentence stops offering it.
    /// </summary>
    /// <remarks>
    /// The blurb offered "All time" from every unsearched state, so widening to all time on
    /// a group with nothing in it produced "Nothing in all time. This tab opens on the
    /// current month -- pick 'All time' for the whole ledger": advice to do the thing they
    /// had just done, under a heading that already said the group was empty.
    /// </remarks>
    [Fact]
    public async Task All_time_is_not_offered_to_somebody_already_on_it()
    {
        var tab = Render();

        await PickSpanAsync(tab, DateFilterPreset.AllTime);

        var (heading, blurb) = Empty(tab);

        Assert.Equal("Nothing recorded yet", heading);
        Assert.DoesNotContain("All time", blurb);
        Assert.DoesNotContain("all time", blurb);
    }

    /// <summary>
    /// No span to name means no span in the sentence, rather than the words "all time"
    /// dropped into the slot a month goes in.
    /// </summary>
    [Fact]
    public async Task A_kind_over_all_time_names_no_span()
    {
        var tab = Render();

        await ClickChipAsync(tab, "Settlements");
        await PickSpanAsync(tab, DateFilterPreset.AllTime);

        Assert.Equal("No settlements.", Empty(tab).Blurb);
    }

    /// <summary>A span somebody picked is a filter, and the heading treats it as one.</summary>
    [Fact]
    public async Task A_span_somebody_picked_is_read_as_a_filter()
    {
        var tab = Render();

        await PickSpanAsync(tab, DateFilterPreset.LastMonth);

        var (heading, blurb) = Empty(tab);

        Assert.Equal("Nothing matches", heading);
        Assert.Equal("Nothing in last month.", blurb);
    }

    // ---- opening one entry --------------------------------------------------------------

    /// <summary>
    /// Clicking an expense's row opens its details; clicking a settlement's opens nothing.
    /// </summary>
    /// <remarks>
    /// The row is the way in. It used to be an eye in the actions column, beside Edit and
    /// Delete, which made opening an entry look like one more thing to do to it. A
    /// settlement's row is inert on the same grounds the home page's lists leave it so: a
    /// settlement is who paid whom and how much, all three of which are already on the row,
    /// and the dialog is mostly the division of an expense between people. The cursor says
    /// which rows open, so nobody clicks a settlement to find out.
    /// </remarks>
    [Fact]
    public async Task An_expense_row_opens_its_details_and_a_settlement_row_does_not()
    {
        var expense = Expense(Guid.NewGuid());
        _entries = [expense, Transfer(Guid.NewGuid())];

        Guid? viewed = null;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<TransactionDetailsDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<TransactionDetailsDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters<TransactionDetailsDialog> parameters, DialogOptions _) =>
            {
                viewed = parameters.Get<Guid>(nameof(TransactionDetailsDialog.TransactionId));

                return new DialogReference(Guid.NewGuid(), _dialogs.Object);
            });

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Equal(2, Buttons(tab, "Edit ").Count));

        // No eye any more, on either row.
        Assert.Empty(Buttons(tab, "View "));

        Assert.Equal(2, tab.FindAll("tbody tr.mud-table-row").Count);

        Assert.Contains("is-openable", ExpenseRow(tab).ClassName);
        Assert.DoesNotContain("is-openable", SettlementRow(tab).ClassName ?? "");

        // Found again before each click: the grid re-renders on a row click, and a row
        // found before it points at handlers that are gone.
        await SettlementRow(tab).ClickAsync(new());
        Assert.Null(viewed);

        await ExpenseRow(tab).ClickAsync(new());
        Assert.Equal(expense.Id, viewed);
    }

    /// <summary>
    /// A settlement made through a day does open: what its period held, and whether it is
    /// still square, are not on the row. Its row says which period it was for.
    /// </summary>
    [Fact]
    public async Task A_settlement_made_through_a_day_opens_and_names_its_period()
    {
        var settled = Transfer(Guid.NewGuid()) with { CoversThrough = new DateOnly(2026, 8, 31) };
        _entries = [settled];

        Guid? viewed = null;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<TransactionDetailsDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<TransactionDetailsDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters<TransactionDetailsDialog> parameters, DialogOptions _) =>
            {
                viewed = parameters.Get<Guid>(nameof(TransactionDetailsDialog.TransactionId));

                return new DialogReference(Guid.NewGuid(), _dialogs.Object);
            });

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Contains("Settling up · through 31 Aug", tab.Markup));
        Assert.Contains("is-openable", SettlementRow(tab).ClassName);

        await SettlementRow(tab).ClickAsync(new());
        Assert.Equal(settled.Id, viewed);
    }

    /// <summary>
    /// The entry's name is a button, so the details are reachable without a pointer.
    /// </summary>
    /// <remarks>
    /// A grid row carries no tabindex and RowClick never hears Enter, so removing the eye
    /// left the one screen about a single expense reachable by mouse alone. The name is what
    /// somebody would press anyway. A settlement's name is not a button, for the reason its
    /// row does not open.
    /// </remarks>
    [Fact]
    public async Task The_entrys_name_opens_its_details_from_the_keyboard()
    {
        var expense = Expense(Guid.NewGuid());
        _entries = [expense, Transfer(Guid.NewGuid())];

        Guid? viewed = null;
        var opened = 0;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<TransactionDetailsDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<TransactionDetailsDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters<TransactionDetailsDialog> parameters, DialogOptions _) =>
            {
                viewed = parameters.Get<Guid>(nameof(TransactionDetailsDialog.TransactionId));
                opened++;

                return new DialogReference(Guid.NewGuid(), _dialogs.Object);
            });

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Single(tab.FindAll("button.gs-row-open")));

        var name = tab.Find("button.gs-row-open");
        Assert.Equal("Dinner", name.TextContent.Trim());

        await name.ClickAsync(new());

        Assert.Equal(expense.Id, viewed);

        // Once, not twice: the click stops at the button rather than reaching the row
        // underneath, which opens the same dialog.
        Assert.Equal(1, opened);
    }

    private static IElement ExpenseRow(IRenderedComponent<GroupLedgerTab> tab) =>
        tab.FindAll("tbody tr.mud-table-row").First(row => row.TextContent.Contains("Dinner"));

    private static IElement SettlementRow(IRenderedComponent<GroupLedgerTab> tab) =>
        tab.FindAll("tbody tr.mud-table-row").First(row => !row.TextContent.Contains("Dinner"));

    /// <summary>
    /// Edit sits on a row that opens when clicked, so its click must not fall through to
    /// the row: pressing Edit used to open the details behind the editor.
    /// </summary>
    [Fact]
    public async Task Pressing_edit_does_not_also_open_the_details()
    {
        _entries = [Expense(Guid.NewGuid())];

        var opened = 0;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<TransactionDetailsDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<TransactionDetailsDialog>>(),
                It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                opened++;
                return new DialogReference(Guid.NewGuid(), _dialogs.Object);
            });

        // Edit opens its own dialog, which is not what this is about; it only has to
        // resolve to something the tab can await and read a "nothing changed" out of.
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<UpdateTransactionDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(() =>
            {
                var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);
                reference.Dismiss(DialogResult.Ok<JsonPatchDocument<UpdateTransactionRequest>?>(null));
                return reference;
            });

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Single(Buttons(tab, "Edit ")));

        await Buttons(tab, "Edit ")[0].ClickAsync(new());

        Assert.Equal(0, opened);
    }

    [Fact]
    public void Ledger_action_tooltips_do_not_remain_open_after_focus()
    {
        _entries = [Expense(Guid.NewGuid())];

        var tab = Render();

        Assert.Equal(2, tab.FindComponents<MudTooltip>().Count);
        Assert.All(tab.FindComponents<MudTooltip>(), tooltip => Assert.False(tooltip.Instance.ShowOnFocus));
    }

    // ---- the day and time on a row (#252) -----------------------------------------------

    /// <summary>New York at UTC-5 and Sydney at UTC+10: either side of Greenwich.</summary>
    public static TheoryData<int> Readers => [-5, 10];

    /// <summary>What the "When" cell of the row named <paramref name="name"/> says, line by line.</summary>
    private static string[] When(IRenderedComponent<GroupLedgerTab> tab, string name) =>
        tab.FindAll("tr").Single(row => row.TextContent.Contains(name, StringComparison.Ordinal))
            .QuerySelector("td")!
            .QuerySelectorAll("div.gs-num")
            .Select(line => line.TextContent.Trim())
            .ToArray();

    /// <summary>
    /// A charge the bank posted on 9 October is stored at midnight UTC, which is 19:00 on the
    /// 8th in New York. It reads as the 9th there and in Sydney, with no time under it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Readers))]
    public void A_bank_row_shows_the_banks_date_and_no_time(int hours)
    {
        ReaderAt(TimeSpan.FromHours(hours));
        _entries =
        [
            Expense(Guid.NewGuid(), "IKEA") with { DateTime = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero) }
        ];

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Contains("IKEA", tab.Markup, StringComparison.Ordinal));
        Assert.Equal([new DateOnly(2026, 10, 9).ToString("d MMM yyyy")], When(tab, "IKEA"));
    }

    /// <summary>
    /// Anything else is a moment and is shown as one: the day and the time it fell on where
    /// the reader is.
    /// </summary>
    [Theory]
    [InlineData(-5, 8, 13)]
    [InlineData(10, 9, 4)]
    public void A_recorded_moment_shows_its_day_and_time_where_the_reader_is(int hours, int day, int hour)
    {
        ReaderAt(TimeSpan.FromHours(hours));
        _entries =
        [
            Expense(Guid.NewGuid(), "Dinner") with { DateTime = new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.Zero) }
        ];

        var tab = Render();

        tab.WaitForAssertion(() => Assert.Contains("Dinner", tab.Markup, StringComparison.Ordinal));
        Assert.Equal(
            [new DateOnly(2026, 10, day).ToString("d MMM yyyy"), new TimeOnly(hour, 30).ToString("t")],
            When(tab, "Dinner"));
    }
}

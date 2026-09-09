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
                It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid _, DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string _, bool? _,
                string search, CancellationToken _) =>
            {
                var ask = new Ask(from, to, search);
                _asks.Add(ask);

                return _answer(ask);
            });

        _groups
            .Setup(client => client.GetGroupActivityAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, DateTimeOffset? _, DateTimeOffset? _, int? kind, string _, string _,
                bool? _, int? _, int? _, CancellationToken _) =>
            {
                _kinds.Add(kind);

                return new PagedResponseOfGroupLedgerEntryResponse(_entries, 1, 10, _entries.Count);
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
}

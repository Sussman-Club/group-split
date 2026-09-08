using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
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
/// cards showing the all-time figures under a caption naming the range. From Activity: a
/// settlement can be deleted and an expense edited, and neither offers the other's action.
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
        tab.FindAll(".gs-chip").First(chip => chip.TextContent.Contains(label)).ClickAsync(new());

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

    // ---- the figures over the list ------------------------------------------------------

    [Fact]
    public void The_cards_start_on_the_whole_group()
    {
        var tab = Render();

        Assert.Equal(("5", "$29.00"), Cards(tab));
        Assert.True(Assert.Single(_asks).IsAllTime);
    }

    [Fact]
    public async Task Picking_a_range_asks_about_that_range_and_shows_what_comes_back()
    {
        _answer = ask => Task.FromResult(ask.IsAllTime
            ? new TransactionSummaryResponse(5, 29m)
            : new TransactionSummaryResponse(0, 0m));

        var tab = Render();

        await ClickChipAsync(tab, "Last month");

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
    /// the group, and an all-time answer from before a chip was clicked could land after the
    /// filtered one -- putting the all-time figures back on cards captioned with the range.
    /// </summary>
    [Fact]
    public async Task An_all_time_answer_that_arrives_late_does_not_land_on_the_cards()
    {
        var allTimeAnswered = new TaskCompletionSource<TransactionSummaryResponse>();

        _answer = ask => ask.IsAllTime
            ? allTimeAnswered.Task
            : Task.FromResult(new TransactionSummaryResponse(0, 0m));

        var tab = Render();

        // The first read is still in flight, the way a cold start leaves it. The range is
        // picked while it is, which is the ordinary way to use the page.
        await ClickChipAsync(tab, "Last month");

        Assert.Equal(("0", "$0.00"), Cards(tab));

        // Now the stale one answers. The component resumes on the renderer's dispatcher, so
        // it is given its turns there before anything is looked at -- asserting straight
        // after the result is set would pass by having looked too early to see the damage.
        allTimeAnswered.SetResult(new TransactionSummaryResponse(5, 29m));

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
        Assert.True(_asks.Last().IsAllTime);
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
    /// Each kind of row offers what can actually be done to it. An expense is edited and
    /// deleted; a settlement has nothing to it but who paid whom and how much, so
    /// re-recording it is the whole correction and there is no edit to offer.
    /// </summary>
    [Fact]
    public void A_settlement_offers_a_delete_and_no_edit_and_an_expense_offers_both()
    {
        _entries = [Transfer(Guid.NewGuid()), Expense(Guid.NewGuid())];

        var tab = Render();

        // Named after the row rather than "Delete": there is one of these per entry and
        // they are otherwise indistinguishable to a screen reader.
        var deletes = Buttons(tab, "Delete");
        var edits = Buttons(tab, "Edit");

        Assert.Equal(2, deletes.Count);
        Assert.Contains(deletes, button => button.GetAttribute("aria-label")!.Contains("Anabel paid Daniel"));

        var edit = Assert.Single(edits);
        Assert.Contains("Dinner", edit.GetAttribute("aria-label"));
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
}

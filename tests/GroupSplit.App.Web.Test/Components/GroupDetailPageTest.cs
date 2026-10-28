using Bunit;
using Bunit.TestDoubles;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The group page: the shell around a group's four tabs, which picks the group from the
/// route and the tab from the path.
/// </summary>
/// <remarks>
/// The tabs are stubbed. Each has its own test file, and what is pinned here is the page's
/// part: that the URL selects the group and reads its figures, that every old section name
/// still lands somewhere, that a group which is not the reader's says so instead of
/// rendering an empty page, and that the two writes the page owns -- adding an expense and
/// settling with somebody -- go to the group the dialog was opened on (#323).
/// </remarks>
public class GroupDetailPageTest : ComponentTest
{
    private static readonly GroupResponse Lisbon = new(Guid.NewGuid(), "Weekend in Lisbon", 3) { Currency = "EUR" };
    private static readonly GroupResponse Flat = new(Guid.NewGuid(), "The flat", 2);

    private readonly Mock<IGroupsPageStateService> _state = new();
    private readonly Mock<IDialogService> _dialogs = new();

    /// <summary>The reader's groups, as the state service holds them.</summary>
    private List<GroupResponse> _groups = [Lisbon, Flat];

    /// <summary>Whether the reader's groups have been read. Already done unless a test holds it.</summary>
    private readonly TaskCompletionSource _ready = new();

    public GroupDetailPageTest()
    {
        _ready.SetResult();
        _state.SetupGet(state => state.Balance).Returns(new UserGroupBalanceResponse());
        _state.SetupGet(state => state.Transactions).Returns(new PagedResponseOfTransactionResponse([], 1, 8, 0));

        _state.SetupGet(state => state.Groups).Returns(() => _groups);
        _state.SetupProperty(state => state.SelectedGroup, Flat);
        _state.SetupGet(state => state.IsReadyTask).Returns(() => _ready.Task);
        _state.Setup(state => state.EnsureSelectedLoadedAsync()).Returns(Task.CompletedTask);

        Services.AddSingleton(_state.Object);
        Services.AddSingleton(_dialogs.Object);

        ComponentFactories.AddStub<GroupOverviewTab>();
        ComponentFactories.AddStub<GroupLedgerTab>();
        ComponentFactories.AddStub<GroupMembersTab>();
        ComponentFactories.AddStub<GroupSettingsTab>();
    }

    private IRenderedComponent<GroupDetail> Open(Guid groupId, string? tab = null) =>
        Render<GroupDetail>(parameters => parameters
            .Add(page => page.GroupId, groupId)
            .Add(page => page.Tab, tab));

    [Fact]
    public void Pending_group_figures_do_not_instantiate_the_tab()
    {
        var ready = new TaskCompletionSource();
        _state.Setup(state => state.EnsureSelectedLoadedAsync()).Returns(ready.Task);

        var page = Open(Lisbon.Id);

        Assert.Null(Section(page));
        Assert.Single(page.FindAll("[role='status'][aria-busy='true']"));
        ready.SetResult();
        page.WaitForAssertion(() => Assert.Equal("balances", Section(page)));
    }

    /// <summary>Which of the four tabs the page is showing, or null for none of them.</summary>
    private static string? Section(IRenderedComponent<GroupDetail> page) =>
        page.HasComponent<Stub<GroupOverviewTab>>() ? "balances"
        : page.HasComponent<Stub<GroupLedgerTab>>() ? "activity"
        : page.HasComponent<Stub<GroupMembersTab>>() ? "people"
        : page.HasComponent<Stub<GroupSettingsTab>>() || page.FindAll(".gs-group-settings-overview").Count > 0 ? "settings"
        : null;

    /// <summary>A dialog that has already been answered, the state a component is handed after a click.</summary>
    private DialogReference Answered(DialogResult result)
    {
        var reference = new DialogReference(Guid.NewGuid(), _dialogs.Object);

        reference.Dismiss(result);

        return reference;
    }

    [Fact]
    public void The_group_in_the_url_is_selected_and_its_figures_read()
    {
        var page = Open(Lisbon.Id);

        Assert.Equal(Lisbon, _state.Object.SelectedGroup);
        _state.Verify(state => state.EnsureSelectedLoadedAsync(), Times.Once);

        Assert.Equal("Weekend in Lisbon", page.Find("h1").TextContent.Trim());
        Assert.Contains("3 members", page.Find(".gs-identity-who p").TextContent, StringComparison.Ordinal);

        var overview = page.FindComponent<Stub<GroupOverviewTab>>();
        Assert.Equal(Lisbon.Id, overview.Instance.Parameters.Get(tab => tab.GroupId));
    }

    /// <summary>
    /// A selection that already matches is not proof the figures beside it do: the
    /// prerender persists the two separately, and taking a match as read left the previous
    /// group's figures on the page.
    /// </summary>
    [Fact]
    public void A_group_already_selected_still_has_its_figures_read()
    {
        _state.Object.SelectedGroup = Lisbon;

        Open(Lisbon.Id);

        _state.Verify(state => state.EnsureSelectedLoadedAsync(), Times.Once);
    }

    /// <summary>
    /// The tab is in the URL, and the names sections have had before still resolve, so a
    /// saved link to a group's expenses, members or rules lands in the same place.
    /// </summary>
    [Theory]
    [InlineData(null, "balances")]
    [InlineData("balances", "balances")]
    [InlineData("activity", "activity")]
    [InlineData("ledger", "activity")]
    [InlineData("expenses", "activity")]
    [InlineData("people", "people")]
    [InlineData("members", "people")]
    [InlineData("PEOPLE", "people")]
    [InlineData("settings", "settings")]
    [InlineData("rules", "settings")]
    [InlineData("splits", "settings")]
    [InlineData("categories", "settings")]
    [InlineData("no-such-section", "balances")]
    public void The_path_decides_which_tab_opens(string? tab, string expected)
    {
        var page = Open(Lisbon.Id, tab);

        Assert.Equal(expected, Section(page));

        var current = page.Find("nav.gs-tabs a.is-current");
        var href = expected == "balances" ? $"/groups/{Lisbon.Id}" : $"/groups/{Lisbon.Id}/{(expected == "people" ? "settings" : expected)}";

        Assert.Equal(href, current.GetAttribute("href"));
        Assert.Equal("page", current.GetAttribute("aria-current"));
    }

    [Fact]
    public void Settings_overview_links_to_each_section()
    {
        var page = Open(Lisbon.Id, "settings");
        var links = page.FindAll(".gs-group-settings-choices a").Select(link => link.GetAttribute("href")).ToArray();
        Assert.Equal(new[] { $"/groups/{Lisbon.Id}/details", $"/groups/{Lisbon.Id}/people", $"/groups/{Lisbon.Id}/categories", $"/groups/{Lisbon.Id}/rules" }, links);
    }

    [Theory]
    [InlineData("details")]
    [InlineData("people")]
    [InlineData("categories")]
    [InlineData("rules")]
    public void Settings_sections_offer_a_link_back(string tab)
    {
        var page = Open(Lisbon.Id, tab);
        Assert.Equal($"/groups/{Lisbon.Id}/settings", page.Find("a.gs-settings-back").GetAttribute("href"));
    }

    /// <summary>
    /// The Expenses tab is gone, and its old link opens the ledger narrowed to expenses,
    /// which is what that tab was.
    /// </summary>
    [Fact]
    public void An_old_expenses_link_opens_the_ledger_on_expenses_only()
    {
        var expenses = Open(Lisbon.Id, "expenses").FindComponent<Stub<GroupLedgerTab>>();
        Assert.Equal(ActivityKind.Expense, expenses.Instance.Parameters.Get(tab => tab.Kind));

        var activity = Open(Lisbon.Id, "activity").FindComponent<Stub<GroupLedgerTab>>();
        Assert.Null(activity.Instance.Parameters.Get(tab => tab.Kind));
    }

    /// <summary>
    /// Narrowing the ledger is written into the URL in place: the back button leaves the
    /// group rather than stepping back through every chip. It goes on the tab's own path,
    /// so an old /expenses link stops implying a kind once somebody picks another.
    /// </summary>
    [Fact]
    public async Task Narrowing_the_ledger_rewrites_the_url_without_a_history_entry()
    {
        var page = Open(Lisbon.Id, "expenses");
        var ledger = page.FindComponent<Stub<GroupLedgerTab>>();

        var query = new Dictionary<string, object?> { ["kind"] = "settlement" };
        await page.InvokeAsync(() => ledger.Instance.Parameters.Get(tab => tab.QueryChanged).InvokeAsync(query));

        var nav = Services.GetRequiredService<BunitNavigationManager>();
        var last = nav.History.First();

        Assert.EndsWith($"/groups/{Lisbon.Id}/activity?kind=settlement", last.Uri, StringComparison.Ordinal);
        Assert.True(last.Options.ReplaceHistoryEntry);
    }

    /// <summary>
    /// A personal group has nobody to owe or invite, so it is its activity and its
    /// settings, and a link to its balances or people opens the activity instead.
    /// </summary>
    [Theory]
    [InlineData(null, "activity")]
    [InlineData("balances", "activity")]
    [InlineData("people", "activity")]
    [InlineData("settings", "settings")]
    [InlineData("categories", "settings")]
    public void A_personal_group_opens_on_its_activity_and_offers_nothing_else_but_settings(string? tab, string expected)
    {
        var mine = new GroupResponse(Guid.NewGuid(), "Personal", 1) { IsPersonal = true };
        _groups = [mine, Lisbon];

        var page = Open(mine.Id, tab);

        Assert.Equal(expected, Section(page));

        var sections = page.FindAll("nav.gs-tabs a").Select(link => link.GetAttribute("href")).ToList();
        Assert.Equal([$"/groups/{mine.Id}", $"/groups/{mine.Id}/settings"], sections);

        Assert.Equal("Just you", page.Find(".gs-eyebrow").TextContent.Trim());
        Assert.Contains("Your own spending", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header says when something last happened rather than how many expenses there
    /// are: a count does not say whether the group is still running.
    /// </summary>
    [Fact]
    public void The_header_says_when_something_last_happened()
    {
        var clock = ReaderAt(TimeSpan.Zero);
        var threeDaysAgo = new DateTimeOffset(clock.Today.AddDays(-3).AddHours(12), TimeSpan.Zero);

        _state.SetupGet(state => state.Transactions).Returns(new PagedResponse<TransactionResponse>(
            [
                new TransactionResponse { Id = Guid.NewGuid(), Name = "Groceries", DateTime = threeDaysAgo.AddDays(-20) },
                new TransactionResponse { Id = Guid.NewGuid(), Name = "Dinner", DateTime = threeDaysAgo }
            ], 1, 10, 2));

        var page = Open(Lisbon.Id);

        Assert.Equal("3 members · last activity 3 days ago",
            page.Find(".gs-identity-who p").TextContent.Trim());
    }

    [Fact]
    public void An_archived_group_says_so_and_offers_no_add_expense_button()
    {
        var archived = new GroupResponse(Guid.NewGuid(), "Ski trip 2024", 4, IsArchive: true);
        _groups = [archived];

        var page = Open(archived.Id);

        Assert.Equal("Archived group", page.Find(".gs-eyebrow").TextContent.Trim());
        Assert.Empty(page.FindAll("button.gs-fab"));
    }

    // ---- a group that is not the reader's -------------------------------------------------

    /// <summary>
    /// A group the reader is not in -- deleted, left, or never theirs -- is not among the
    /// groups the server gave them, and the page says so rather than rendering a header
    /// with nothing under it. Nothing is selected and nothing is read.
    /// </summary>
    [Fact]
    public void A_group_that_is_not_yours_says_so_and_reads_nothing()
    {
        var page = Open(Guid.NewGuid());

        Assert.Contains("That group is not yours", page.Markup, StringComparison.Ordinal);
        Assert.Equal("/groups", page.FindAll("a").Single(link =>
            link.TextContent.Trim() == "Back to your groups").GetAttribute("href"));

        Assert.Null(Section(page));
        Assert.Empty(page.FindAll("button.gs-fab"));
        Assert.Equal(Flat, _state.Object.SelectedGroup);
        _state.Verify(state => state.EnsureSelectedLoadedAsync(), Times.Never);
    }

    /// <summary>
    /// "Not yours" is only an answer once the groups have been read. Before that, an empty
    /// list means nothing yet, and saying the group is not theirs would be a flash of a
    /// wrong answer on every cold load.
    /// </summary>
    [Fact]
    public async Task Until_your_groups_are_read_the_page_does_not_say_the_group_is_not_yours()
    {
        var ready = new TaskCompletionSource();
        _state.SetupGet(state => state.IsReadyTask).Returns(ready.Task);
        _groups = [];

        var page = Open(Lisbon.Id);

        Assert.DoesNotContain("That group is not yours", page.Markup, StringComparison.Ordinal);
        Assert.Null(Section(page));

        _groups = [Lisbon, Flat];
        await page.InvokeAsync(ready.SetResult);

        page.WaitForAssertion(() => Assert.Equal("balances", Section(page)));
        Assert.Equal(Lisbon, _state.Object.SelectedGroup);
    }

    /// <summary>
    /// A group that goes while its page is open -- left from the People tab, say -- turns
    /// into "not yours" when the list is re-read, rather than into a page with nothing on
    /// it. The state service moves the selection on to another group when that happens.
    /// </summary>
    [Fact]
    public void A_group_left_while_its_page_is_open_turns_into_not_yours()
    {
        var page = Open(Lisbon.Id, "people");

        Assert.Equal("people", Section(page));

        _groups = [Flat];
        _state.Object.SelectedGroup = Flat;
        _state.Raise(state => state.OnGroupsChanged += null);

        page.WaitForAssertion(() =>
            Assert.Contains("That group is not yours", page.Markup, StringComparison.Ordinal));
        Assert.Null(Section(page));
    }

    // ---- the writes the page owns ---------------------------------------------------------

    [Fact]
    public async Task Adding_an_expense_opens_the_dialog_on_this_group_and_records_what_it_returns()
    {
        var request = new CreateTransactionRequest { GroupId = Lisbon.Id, Name = "Dinner", Amount = 48m };
        DialogParameters<CreateTransactionDialog>? asked = null;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<CreateTransactionDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<CreateTransactionDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters<CreateTransactionDialog> parameters, DialogOptions _) =>
            {
                asked = parameters;
                return Answered(DialogResult.Ok(request));
            });

        var page = Open(Lisbon.Id);

        await page.Find("button.gs-fab").ClickAsync(new());

        Assert.NotNull(asked);
        Assert.Equal(Lisbon.Id, asked.Get<Guid?>(nameof(CreateTransactionDialog.SelectedGroupId)));
        Assert.Equal("Weekend in Lisbon", asked.Get<string?>(nameof(CreateTransactionDialog.SelectedGroupName)));
        Assert.Equal("EUR", asked.Get<string?>(nameof(CreateTransactionDialog.SelectedGroupCurrency)));

        _state.Verify(state => state.CreateTransactionAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_cancelled_expense_records_nothing()
    {
        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<CreateTransactionDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<CreateTransactionDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(() => Answered(DialogResult.Cancel()));

        var page = Open(Lisbon.Id);

        await page.Find("button.gs-fab").ClickAsync(new());

        _state.Verify(state => state.CreateTransactionAsync(It.IsAny<CreateTransactionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The Balances tab says which line was pressed; the page owns the dialog and the write.
    /// </summary>
    [Fact]
    public async Task Settling_from_the_balances_tab_asks_about_that_person_and_records_the_answer()
    {
        var omar = new DebtInfo { UserId = Guid.NewGuid(), UserName = "Omar Rivero", Amount = 25m };
        var request = new SettleRequest { UserId = omar.UserId, Amount = 25m };
        DialogParameters<SettleDialog>? asked = null;

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<SettleDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<SettleDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync((string _, DialogParameters<SettleDialog> parameters, DialogOptions _) =>
            {
                asked = parameters;
                return Answered(DialogResult.Ok(request));
            });

        var page = Open(Lisbon.Id);
        var settle = page.FindComponent<Stub<GroupOverviewTab>>().Instance.Parameters.Get(tab => tab.OnSettle);

        await page.InvokeAsync(() => settle.InvokeAsync((omar, SettlementDirection.YouPaidThem)));

        Assert.NotNull(asked);
        Assert.Equal(Lisbon.Id, asked.Get<Guid>(nameof(SettleDialog.GroupId)));
        Assert.Equal(omar.UserId, asked.Get<Guid>(nameof(SettleDialog.UserId)));
        Assert.Equal(25m, asked.Get<decimal>(nameof(SettleDialog.TotalDebt)));
        Assert.Equal(SettlementDirection.YouPaidThem, asked.Get<SettlementDirection>(nameof(SettleDialog.Direction)));

        _state.Verify(state => state.SettleAsync(Lisbon.Id, request, "Omar Rivero", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// #323: a settlement opened on one group and confirmed after the route had moved to
    /// another was recorded in the second. The repayment belongs to the group the dialog
    /// was about.
    /// </summary>
    [Fact]
    public async Task A_settlement_confirmed_after_moving_to_another_group_is_recorded_in_the_first()
    {
        var omar = new DebtInfo { UserId = Guid.NewGuid(), UserName = "Omar Rivero", Amount = 25m };
        var request = new SettleRequest { UserId = omar.UserId, Amount = 25m };
        var dialog = new DialogReference(Guid.NewGuid(), _dialogs.Object);

        _dialogs
            .Setup(dialogs => dialogs.ShowAsync<SettleDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<SettleDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(dialog);

        var page = Open(Lisbon.Id);
        var settle = page.FindComponent<Stub<GroupOverviewTab>>().Instance.Parameters.Get(tab => tab.OnSettle);

        // Held rather than awaited: the handler is waiting on the dialog it just opened.
        var settling = page.InvokeAsync(() => settle.InvokeAsync((omar, SettlementDirection.TheyPaidYou)));

        page.Render(parameters => parameters.Add(component => component.GroupId, Flat.Id));
        Assert.Equal(Flat, _state.Object.SelectedGroup);

        dialog.Dismiss(DialogResult.Ok(request));
        await settling;

        _state.Verify(state => state.SettleAsync(Lisbon.Id, request, "Omar Rivero", It.IsAny<CancellationToken>()),
            Times.Once);
        _state.Verify(state => state.SettleAsync(Flat.Id, It.IsAny<SettleRequest>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}

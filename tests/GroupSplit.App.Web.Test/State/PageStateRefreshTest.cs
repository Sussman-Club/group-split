using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using Microsoft.JSInterop;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// The two page states are copies of the same server, kept by different pages. These pin
/// that a write made through either of them, or announced by a dialog that wrote on its
/// own, reaches both -- the bug they were written against was an expense edited from the
/// groups page whose new amount did not show until the page was reloaded.
/// </summary>
public class PageStateRefreshTest
{
    // ---- A server in memory ------------------------------------------------------------------
    //
    // The generated clients are mocked over these lists, and every write mutates them, so
    // a state service only sees the change if it actually re-reads.

    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Trip = Guid.NewGuid();
    private static readonly Guid Flat = Guid.NewGuid();

    private readonly List<GroupResponse> _groups =
    [
        new(Trip, "Weekend in Lisbon", 3),
        new(Flat, "Flat", 2)
    ];

    private readonly List<TransactionResponse> _transactions =
    [
        Expense(Trip, "Weekend in Lisbon", "Dinner", 96m),
        Expense(Trip, "Weekend in Lisbon", "Taxi", 24m),
        Expense(Flat, "Flat", "Groceries", 40m)
    ];

    private static TransactionResponse Expense(Guid groupId, string groupName, string name, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Amount = amount,
        DateTime = DateTimeOffset.UtcNow,
        GroupId = groupId,
        GroupName = groupName,
        PaidByUserId = Me,
        PaidByUserName = "Me"
    };

    private UserGroupBalanceResponse BalanceOf(Guid groupId) => new()
    {
        NetBalances =
        [
            new GroupNetBalance
            {
                UserId = Me,
                UserName = "Me",
                Balance = _transactions.Where(t => t.GroupId == groupId).Sum(t => t.Amount)
            }
        ]
    };

    /// <summary>
    /// A server that really pages, so the states can be held to what they ask for rather
    /// than to what they would get from a list handed over whole.
    /// </summary>
    private static PagedResponseOfTransactionResponse Page(
        IEnumerable<TransactionResponse> scope, DateTimeOffset? from, DateTimeOffset? to, string? search,
        string? sortBy, bool? sortDescending, int? page, int? pageSize)
    {
        var matched = Within(Search(scope, search), from, to).ToList();

        var descending = sortDescending ?? true;

        IEnumerable<TransactionResponse> sorted = sortBy switch
        {
            "amount" => descending
                ? matched.OrderByDescending(t => t.Amount)
                : matched.OrderBy(t => t.Amount),
            "name" => descending ? matched.OrderByDescending(t => t.Name) : matched.OrderBy(t => t.Name),
            _ => descending
                ? matched.OrderByDescending(t => t.DateTime)
                : matched.OrderBy(t => t.DateTime)
        };

        var size = pageSize ?? PageRequest.DefaultPageSize;
        var number = page ?? 1;

        var items = sorted.Skip((number - 1) * size).Take(size).ToList();

        return new PagedResponseOfTransactionResponse(items, number, size, matched.Count);
    }

    private static IEnumerable<TransactionResponse> Within(
        IEnumerable<TransactionResponse> scope, DateTimeOffset? from, DateTimeOffset? to) =>
        scope.Where(t => (from is null || t.DateTime >= from) && (to is null || t.DateTime <= to));

    private static IEnumerable<TransactionResponse> Search(IEnumerable<TransactionResponse> scope, string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? scope
            : scope.Where(t =>
                t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.GroupName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.PaidByUserName.Contains(search, StringComparison.OrdinalIgnoreCase));

    private static TransactionSummaryResponse SummaryOf(
        IEnumerable<TransactionResponse> scope, string? search, DateTimeOffset? from, DateTimeOffset? to)
    {
        var matched = Within(Search(scope, search), from, to).ToList();

        return new TransactionSummaryResponse(matched.Count, matched.Sum(t => t.Amount));
    }

    // ---- The clients, the presenter and the two states over it -----------------------------

    private readonly Mock<ITransactionsClient> _transactionsClient = new();
    private readonly Mock<IGroupsClient> _groupsClient = new();
    private readonly Mock<IUsersClient> _usersClient = new();
    private readonly Mock<IInvitationsClient> _invitationsClient = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly TransactionsPageStateService _expensesPage;
    private readonly GroupsPageStateService _groupsPage;

    public PageStateRefreshTest()
    {
        _transactionsClient
            .Setup(c => c.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string? _, bool? personal,
                    string? search, string? sortBy, bool? sortDescending, int? page, int? pageSize,
                    CancellationToken _) =>
                Page(Scoped(_transactions.Where(t => t.PaidByUserId == Me), personal), from, to, search, sortBy,
                    sortDescending, page, pageSize));

        _transactionsClient
            .Setup(c => c.GetTransactionsSummaryAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string? _, bool? personal,
                    string? search, CancellationToken _) =>
                SummaryOf(Scoped(_transactions.Where(t => t.PaidByUserId == Me), personal), search, from, to));

        _transactionsClient
            .Setup(c => c.CreateTransactionAsync(It.IsAny<CreateTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTransactionRequest request, CancellationToken _) =>
            {
                var group = _groups.Single(g => g.Id == request.GroupId);
                var created = Expense(group.Id, group.Name, request.Name, request.Amount);
                _transactions.Add(created);
                return created;
            });

        // Nothing is linked to a bank here, so recording an expense finds no imported row
        // that could be the same money. Set up all the same, because the command asks after
        // every create and an unconfigured mock answers null.
        _transactionsClient
            .Setup(c => c.GetTransactionBankMatchesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _transactionsClient
            .Setup(c => c.UpdateTransactionAsync(It.IsAny<Guid>(),
                It.IsAny<JsonPatchDocument<UpdateTransactionRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, JsonPatchDocument<UpdateTransactionRequest> patch, CancellationToken _) =>
            {
                var index = _transactions.FindIndex(t => t.Id == id);
                var edited = new UpdateTransactionRequest { Amount = _transactions[index].Amount };
                patch.ApplyTo(edited);
                _transactions[index] = _transactions[index] with { Amount = edited.Amount };
                return _transactions[index];
            });

        _transactionsClient
            .Setup(c => c.DeleteTransactionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) =>
            {
                _transactions.RemoveAll(t => t.Id == id);
                return Task.CompletedTask;
            });

        _usersClient
            .Setup(c => c.GetCurrentUserPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new UserPositionResponse(0m, 0m, 0m, []));

        _groupsClient
            .Setup(c => c.GetGroupsAsAsyncEnumerable(It.IsAny<CancellationToken>()))
            .Returns(() => _groups.ToList().ToAsyncEnumerable());

        _groupsClient
            .Setup(c => c.GetGroupTransactionsAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string? _,
                    bool? _, string? search, string? sortBy, bool? sortDescending, int? page, int? pageSize,
                    CancellationToken _) =>
                Page(_transactions.Where(t => t.GroupId == id), from, to, search, sortBy, sortDescending,
                    page, pageSize));

        _groupsClient
            .Setup(c => c.GetGroupTransactionsSummaryAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, DateTimeOffset? from, DateTimeOffset? to, Guid? _, Guid? _, string? _,
                    bool? _, string? search, CancellationToken _) =>
                SummaryOf(_transactions.Where(t => t.GroupId == id), search, from, to));

        _groupsClient
            .Setup(c => c.ArchiveGroupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => SetArchived(id, archived: true));

        _groupsClient
            .Setup(c => c.UnarchiveGroupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => SetArchived(id, archived: false));

        _groupsClient
            .Setup(c => c.GetGroupUserBalanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => BalanceOf(id));

        _groupsClient
            .Setup(c => c.CreateGroupAsync(It.IsAny<CreateGroupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateGroupRequest request, CancellationToken _) =>
            {
                var created = new GroupResponse(Guid.NewGuid(), request.Name, 1);
                _groups.Add(created);
                return created;
            });

        _groupsClient
            .Setup(c => c.UpdateGroupAsync(It.IsAny<Guid>(), It.IsAny<JsonPatchDocument<CreateGroupRequest>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, JsonPatchDocument<CreateGroupRequest> patch, CancellationToken _) =>
            {
                var index = _groups.FindIndex(g => g.Id == id);
                var edited = new CreateGroupRequest { Name = _groups[index].Name };
                patch.ApplyTo(edited);
                _groups[index] = _groups[index] with { Name = edited.Name };

                for (var i = 0; i < _transactions.Count; i++)
                    if (_transactions[i].GroupId == id)
                        _transactions[i] = _transactions[i] with { GroupName = edited.Name };

                return _groups[index];
            });

        var presenter = new ApiErrorPresenter(Mock.Of<IAuthService>(), new Mock<NavigationManager>().Object,
            _snackbar.Object);
        var guard = new LoadGuard(presenter);

        // Real commands over the mocked clients, rather than mocked commands: a write and
        // the re-read it triggers are the thing under test, and the announcement that joins
        // them lives in the command.
        var groupCommands = new GroupCommands(_groupsClient.Object, _invitationsClient.Object, presenter,
            _snackbar.Object, _changes);
        var transactionCommands = new TransactionCommands(_transactionsClient.Object, presenter,
            _snackbar.Object, Mock.Of<IDialogService>(), _changes);

        // No JS behind it, so the clock falls back to the runtime's own offset -- which is
        // what the "this month" assertion below compares against too.
        _expensesPage = new TransactionsPageStateService(_transactionsClient.Object, new TransactionsTracker(),
            guard, _changes, transactionCommands, new LocalClock(Mock.Of<IJSRuntime>()));
        _groupsPage = new GroupsPageStateService(new GroupsTracker(), _groupsClient.Object, _usersClient.Object,
            guard, presenter, _changes, groupCommands, transactionCommands);
    }

    private GroupResponse SetArchived(Guid id, bool archived)
    {
        var index = _groups.FindIndex(g => g.Id == id);
        _groups[index] = _groups[index] with { IsArchive = archived };
        return _groups[index];
    }

    /// <summary>
    /// The personal filter, as the fake listing applies it: null keeps everything, true
    /// keeps what belongs to no group, false keeps what belongs to one.
    /// </summary>
    private static IEnumerable<TransactionResponse> Scoped(
        IEnumerable<TransactionResponse> transactions, bool? personal) =>
        personal switch
        {
            null => transactions,
            true => transactions.Where(t => t.GroupId is null),
            false => transactions.Where(t => t.GroupId is not null)
        };

    private Task ReadyAsync() => Task.WhenAll(_expensesPage.IsReadyTask, _groupsPage.IsReadyTask);

    private int ListingReads =>
        _transactionsClient.Invocations.Count(i => i.Method.Name == nameof(ITransactionsClient.GetTransactionsAsync));

    private int BalanceReads =>
        _groupsClient.Invocations.Count(i => i.Method.Name == nameof(IGroupsClient.GetGroupUserBalanceAsync));

    private static JsonPatchDocument<UpdateTransactionRequest> AmountPatch(decimal amount)
    {
        var patch = new JsonPatchDocument<UpdateTransactionRequest>();
        patch.Replace(x => x.Amount, amount);
        return patch;
    }

    // ---- Expenses ---------------------------------------------------------------------------

    [Fact]
    public async Task Editing_an_amount_on_the_expenses_page_shows_on_the_groups_page_without_a_reload()
    {
        await ReadyAsync();
        Assert.Equal(Trip, _groupsPage.SelectedGroup?.Id);
        var dinner = _expensesPage.Page!.Items.Single(t => t.Name == "Dinner");
        var balanceReadsBefore = BalanceReads;

        var done = await _expensesPage.UpdateAsync(dinner, AmountPatch(120m));

        Assert.True(done);
        Assert.Equal(120m, _expensesPage.Page!.Items.Single(t => t.Name == "Dinner").Amount);
        Assert.Equal(120m, _groupsPage.Transactions!.Items.Single(t => t.Name == "Dinner").Amount);
        Assert.Equal(144m, _groupsPage.Balance!.NetBalances.Single().Balance);
        Assert.Equal(balanceReadsBefore + 1, BalanceReads);
    }

    [Fact]
    public async Task Adding_an_expense_on_the_groups_page_shows_on_the_expenses_page()
    {
        await ReadyAsync();

        var done = await _groupsPage.CreateTransactionAsync(new CreateTransactionRequest
        {
            GroupId = Trip, Name = "Museum", Amount = 30m
        });

        Assert.True(done);
        Assert.Contains(_expensesPage.Page!.Items, t => t.Name == "Museum");
        Assert.Contains(_groupsPage.Transactions!.Items, t => t.Name == "Museum");
        Assert.Equal(150m, _groupsPage.Balance!.NetBalances.Single().Balance);
    }

    [Fact]
    public async Task Deleting_an_expense_removes_it_from_both_pages()
    {
        await ReadyAsync();
        var taxi = _expensesPage.Page!.Items.Single(t => t.Name == "Taxi");

        var done = await _expensesPage.DeleteAsync(taxi);

        Assert.True(done);
        Assert.DoesNotContain(_expensesPage.Page!.Items, t => t.Id == taxi.Id);
        Assert.DoesNotContain(_groupsPage.Transactions!.Items, t => t.Id == taxi.Id);
        Assert.Equal(96m, _groupsPage.Balance!.NetBalances.Single().Balance);
    }

    /// <summary>
    /// The details dialog writes through the client itself and announces the change; this
    /// is that announcement, with the write already on the server.
    /// </summary>
    [Fact]
    public async Task A_change_announced_by_a_dialog_reaches_both_pages()
    {
        await ReadyAsync();
        var index = _transactions.FindIndex(t => t.Name == "Dinner");
        _transactions[index] = _transactions[index] with { Amount = 200m };

        await _changes.NotifyTransactionsChangedAsync();

        Assert.Equal(200m, _expensesPage.Page!.Items.Single(t => t.Name == "Dinner").Amount);
        Assert.Equal(200m, _groupsPage.Transactions!.Items.Single(t => t.Name == "Dinner").Amount);
        Assert.Equal(224m, _groupsPage.Balance!.NetBalances.Single().Balance);
    }

    [Fact]
    public async Task Both_pages_are_told_to_render_once_the_change_has_landed()
    {
        await ReadyAsync();
        var expensesRendered = 0;
        var groupsRendered = 0;
        _expensesPage.OnTransactionsChanged += () => expensesRendered++;
        _groupsPage.OnTransactionsChanged += () => groupsRendered++;

        await _changes.NotifyTransactionsChangedAsync();

        Assert.Equal(1, expensesRendered);
        Assert.True(groupsRendered >= 1);
    }

    /// <summary>
    /// The data grid on the expenses page only looks at its items again when the reference
    /// changes; a list edited in place left the old amount on screen.
    /// </summary>
    [Fact]
    public async Task A_refresh_hands_out_a_new_list_rather_than_editing_the_old_one()
    {
        await ReadyAsync();
        var expensesBefore = _expensesPage.Page;
        var groupBefore = _groupsPage.Transactions;
        var dinner = expensesBefore!.Items.Single(t => t.Name == "Dinner");

        await _expensesPage.UpdateAsync(dinner, AmountPatch(1m));

        Assert.NotSame(expensesBefore, _expensesPage.Page);
        Assert.NotSame(groupBefore, _groupsPage.Transactions);
    }

    // ---- Groups -----------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_group_lands_on_it_with_its_own_figures()
    {
        await ReadyAsync();

        var done = await _groupsPage.CreateGroupAsync(new CreateGroupRequest { Name = "Five-a-side" });

        Assert.True(done);
        var created = Assert.Single(_groupsPage.Groups, g => g.Name == "Five-a-side");
        Assert.Equal(created.Id, _groupsPage.SelectedGroup?.Id);
        Assert.Empty(_groupsPage.Transactions!.Items);
        Assert.Equal(0, _groupsPage.Transactions.TotalCount);
        Assert.Equal(0m, _groupsPage.Balance!.NetBalances.Single().Balance);
    }

    [Fact]
    public async Task Renaming_a_group_renames_it_on_every_expense_row()
    {
        await ReadyAsync();
        var patch = new JsonPatchDocument<CreateGroupRequest>();
        patch.Replace(x => x.Name, "Lisbon 2026");

        var done = await _groupsPage.UpdateGroupAsync(patch);

        Assert.True(done);
        Assert.Equal("Lisbon 2026", _groupsPage.SelectedGroup?.Name);
        Assert.Equal("Lisbon 2026", Assert.Single(_groupsPage.Groups, g => g.Id == Trip).Name);
        Assert.All(_expensesPage.Page!.Items.Where(t => t.GroupId == Trip),
            t => Assert.Equal("Lisbon 2026", t.GroupName));
    }

    [Fact]
    public async Task A_group_change_keeps_the_selection_by_id_across_the_new_list()
    {
        await ReadyAsync();
        _groupsPage.SelectedGroup = _groupsPage.Groups.Single(g => g.Id == Flat);

        await _changes.NotifyGroupsChangedAsync();

        Assert.Equal(Flat, _groupsPage.SelectedGroup?.Id);
        Assert.Single(_groupsPage.Transactions!.Items);
        Assert.Equal(40m, _groupsPage.Balance!.NetBalances.Single().Balance);
    }

    // ---- Paging and the figures beside it ----------------------------------------------------

    /// <summary>
    /// The grid asks for a page, and what it asks for is what goes to the server -- the
    /// state is not free to answer with something else it happens to be holding.
    /// </summary>
    [Fact]
    public async Task The_query_the_page_is_asked_for_is_the_one_the_server_is_asked()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(new TransactionQuery(
            Page: 2, PageSize: 1, SortBy: "amount", SortDescending: true, Search: "din"));

        _transactionsClient.Verify(c => c.GetTransactionsAsync(
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), "din", "amount", true, 2, 1,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_page_holds_only_its_own_rows_and_says_how_many_there_are_in_all()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(new TransactionQuery(Page: 1, PageSize: 2, SortBy: "amount"));

        var page = _expensesPage.Page!;

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, page.Page);

        // Dearest first: the direction the query asked for.
        Assert.Equal(96m, page.Items[0].Amount);
    }

    [Fact]
    public async Task Asking_again_for_the_page_already_held_does_not_ask_the_server()
    {
        await ReadyAsync();
        var readsBefore = ListingReads;

        await _expensesPage.LoadAsync(_expensesPage.Query);

        Assert.Equal(readsBefore, ListingReads);
    }

    [Fact]
    public async Task Searching_narrows_the_page_and_totals_what_it_matched()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(TransactionQuery.Default with { Search = "din" });

        Assert.Equal("Dinner", Assert.Single(_expensesPage.Page!.Items).Name);
        Assert.Equal(1, _expensesPage.MatchesSummary!.Count);
        Assert.Equal(96m, _expensesPage.MatchesSummary.Total);

        // The all-time figures are what they were: a search narrows the page, not the person.
        Assert.Equal(3, _expensesPage.Summary!.Count);
    }

    [Fact]
    public async Task Without_a_search_there_is_nothing_to_total_separately()
    {
        await ReadyAsync();

        Assert.Null(_expensesPage.MatchesSummary);
    }

    /// <summary>
    /// The tiles say what everything comes to, so they cannot be read off the page: three
    /// expenses of 96, 24 and 40 total 160 whatever page size is in force.
    /// </summary>
    [Fact]
    public async Task The_figures_beside_the_page_count_everything_rather_than_the_page()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(TransactionQuery.Default with { PageSize = 1 });

        Assert.Single(_expensesPage.Page!.Items);
        Assert.Equal(3, _expensesPage.Summary!.Count);
        Assert.Equal(160m, _expensesPage.Summary.Total);
    }

    [Fact]
    public async Task A_write_re_reads_the_page_in_hand_and_the_figures_with_it()
    {
        await ReadyAsync();
        await _expensesPage.LoadAsync(TransactionQuery.Default with { PageSize = 2 });

        var done = await _expensesPage.CreateAsync(new CreateTransactionRequest
        {
            GroupId = Trip, Name = "Museum", Amount = 30m
        });

        Assert.True(done);

        // Still page one of two, and still the query that was asked for.
        Assert.Equal(2, _expensesPage.Query.PageSize);
        Assert.Equal(2, _expensesPage.Page!.Items.Count);
        Assert.Equal(4, _expensesPage.Page.TotalCount);

        Assert.Equal(4, _expensesPage.Summary!.Count);
        Assert.Equal(190m, _expensesPage.Summary.Total);
    }

    [Fact]
    public async Task The_month_figures_ask_for_this_month_and_no_more()
    {
        await ReadyAsync();

        // The exact UTC instants the person's month resolves to, from the same clock the
        // state was built with. Comparing calendar days would not do any more: the bounds
        // are sent as UTC, so midnight where the person is falls on the previous UTC day
        // for anyone east of Greenwich -- which is the whole point of sending them that way.
        var clock = new LocalClock(Mock.Of<IJSRuntime>());
        var month = new DateFilter(DateFilterPreset.ThisMonth).Bounds(clock.Offset, clock.Today);

        _transactionsClient.Verify(c => c.GetTransactionsSummaryAsync(
            // Null-safe: the all-time summary goes through the same method with no dates.
            It.Is<DateTimeOffset?>(from => from == month.From),
            It.Is<DateTimeOffset?>(to => to == month.To),
            It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// The card on the group page shows the newest few, so that is what is fetched -- and
    /// the header's count comes from the total rather than from how many arrived.
    /// </summary>
    [Fact]
    public async Task Selecting_a_group_reads_its_newest_expenses_and_how_many_there_are()
    {
        await ReadyAsync();

        for (var i = 0; i < 10; i++)
            _transactions.Add(Expense(Trip, "Weekend in Lisbon", $"Extra {i}", 5m));

        await _changes.NotifyTransactionsChangedAsync();

        var page = _groupsPage.Transactions!;

        Assert.Equal(GroupsPageStateService.RecentPageSize, page.Items.Count);
        Assert.Equal(12, page.TotalCount);

        _groupsClient.Verify(c => c.GetGroupTransactionsAsync(Trip,
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
            It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<string?>(), TransactionQuery.DefaultSortBy, true,
            1, GroupsPageStateService.RecentPageSize, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ---- Narrowing to a span -----------------------------------------------------------------

    /// <summary>
    /// The span reaches the server as two instants rather than as the name of a preset:
    /// only the client knows what "this month" means where the person is sitting.
    /// </summary>
    [Fact]
    public async Task A_span_is_sent_as_the_two_instants_it_works_out_to()
    {
        await ReadyAsync();

        var range = new DateFilter(DateFilterPreset.ThisMonth);

        await _expensesPage.LoadAsync(TransactionQuery.Default with { Range = range });

        // Resolved against the same clock the state was built with: no JS behind it, so the
        // runtime's own offset and day, which is what the state used too.
        var clock = new LocalClock(Mock.Of<IJSRuntime>());
        var bounds = range.Bounds(clock.Offset, clock.Today);

        _transactionsClient.Verify(c => c.GetTransactionsAsync(
            bounds.From, bounds.To, It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_span_leaves_out_what_falls_outside_it()
    {
        await ReadyAsync();

        // Everything seeded is from today, so last month holds none of it.
        await _expensesPage.LoadAsync(TransactionQuery.Default with
        {
            Range = new DateFilter(DateFilterPreset.LastMonth)
        });

        Assert.Empty(_expensesPage.Page!.Items);
        Assert.Equal(0, _expensesPage.Page.TotalCount);

        await _expensesPage.LoadAsync(TransactionQuery.Default with
        {
            Range = new DateFilter(DateFilterPreset.ThisMonth)
        });

        Assert.Equal(3, _expensesPage.Page!.TotalCount);
    }

    /// <summary>
    /// The figure beside a narrowed page has to describe the same narrowing, or it is
    /// answering a question nobody asked.
    /// </summary>
    [Fact]
    public async Task A_span_is_totalled_over_the_span_and_not_over_everything()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(TransactionQuery.Default with
        {
            Range = new DateFilter(DateFilterPreset.LastMonth)
        });

        Assert.Equal(0, _expensesPage.MatchesSummary!.Count);
        Assert.Equal(0m, _expensesPage.MatchesSummary.Total);

        // The all-time figures are untouched: narrowing the page does not narrow the person.
        Assert.Equal(3, _expensesPage.Summary!.Count);
        Assert.Equal(160m, _expensesPage.Summary.Total);
    }

    [Fact]
    public async Task A_span_and_a_search_narrow_together()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(TransactionQuery.Default with
        {
            Search = "din",
            Range = new DateFilter(DateFilterPreset.ThisMonth)
        });

        Assert.Equal("Dinner", Assert.Single(_expensesPage.Page!.Items).Name);
        Assert.Equal(1, _expensesPage.MatchesSummary!.Count);
    }

    [Fact]
    public async Task Without_a_span_or_a_search_there_is_nothing_to_total_separately()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(TransactionQuery.Default with { Range = DateFilter.AllTime });

        Assert.Null(_expensesPage.MatchesSummary);
    }

    /// <summary>
    /// Two spans chosen the same way are the same value, so asking for the one already on
    /// screen is answered from what is held rather than by a second request.
    /// </summary>
    [Fact]
    public async Task Asking_again_for_the_span_already_shown_does_not_ask_the_server()
    {
        await ReadyAsync();

        await _expensesPage.LoadAsync(TransactionQuery.Default with
        {
            Range = new DateFilter(DateFilterPreset.ThisMonth)
        });

        var readsBefore = ListingReads;

        await _expensesPage.LoadAsync(TransactionQuery.Default with
        {
            Range = new DateFilter(DateFilterPreset.ThisMonth)
        });

        Assert.Equal(readsBefore, ListingReads);
    }

    // ---- Archiving --------------------------------------------------------------------------

    /// <summary>
    /// Archiving is this person's own view of the group, so it is their copy of the list
    /// that changes -- and the group stays selected, because hiding it from a list is not
    /// a change of subject.
    /// </summary>
    [Fact]
    public async Task Archiving_a_group_shows_on_it_and_on_the_list()
    {
        await ReadyAsync();

        var done = await _groupsPage.ArchiveGroupAsync();

        Assert.True(done);
        Assert.True(_groupsPage.SelectedGroup!.IsArchive);
        Assert.True(_groupsPage.Groups.Single(g => g.Id == Trip).IsArchive);

        // Still the group that was selected: archiving is not a change of subject.
        Assert.Equal(Trip, _groupsPage.SelectedGroup.Id);
    }

    [Fact]
    public async Task Unarchiving_takes_it_back()
    {
        await ReadyAsync();
        await _groupsPage.ArchiveGroupAsync();

        var done = await _groupsPage.UnarchiveGroupAsync();

        Assert.True(done);
        Assert.False(_groupsPage.SelectedGroup!.IsArchive);
        // The message names the group. "Group unarchived." told somebody who had just
        // pressed a button the one thing they already knew.
        _snackbar.Verify(sb => sb.Add("Weekend in Lisbon is back in your list.", Severity.Success,
            It.IsAny<Action<SnackbarOptions>?>(), It.IsAny<string?>()), Times.Once);
    }

    /// <summary>
    /// A refusal says why and changes nothing: the list must not show a group as archived
    /// because the app asked, only because the server agreed.
    /// </summary>
    [Fact]
    public async Task A_refused_archive_is_reported_and_leaves_the_group_alone()
    {
        await ReadyAsync();

        _groupsClient
            .Setup(c => c.ArchiveGroupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("nope", 409, "", new Dictionary<string, IEnumerable<string>>(), null));

        var done = await _groupsPage.ArchiveGroupAsync();

        Assert.False(done);
        Assert.False(_groupsPage.SelectedGroup!.IsArchive);
        _snackbar.Verify(sb => sb.Add(It.Is<string>(m => m.StartsWith("Could not archive the group.")),
            Severity.Error, It.IsAny<Action<SnackbarOptions>?>(), It.IsAny<string?>()), Times.Once);
    }

    // ---- Failure ----------------------------------------------------------------------------

    /// <summary>
    /// The write landed; it is the re-read afterwards that failed. Saying "could not update"
    /// would be untrue, and the person might redo a change the server already has.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_is_reported_as_a_load_failure_not_a_failed_write()
    {
        await ReadyAsync();
        var dinner = _expensesPage.Page!.Items.Single(t => t.Name == "Dinner");
        _transactionsClient
            .Setup(c => c.GetTransactionsAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("down", 503, "", new Dictionary<string, IEnumerable<string>>(), null));

        var done = await _expensesPage.UpdateAsync(dinner, AmountPatch(120m));

        Assert.True(done);
        Assert.Equal(120m, _transactions.Single(t => t.Name == "Dinner").Amount);
        _snackbar.Verify(s => s.Add(It.Is<string>(m => m.StartsWith("Could not load your expenses.")),
            Severity.Error, It.IsAny<Action<SnackbarOptions>?>(), It.IsAny<string?>()), Times.Once);
        _snackbar.Verify(s => s.Add(It.Is<string>(m => m.StartsWith("Could not update")),
            It.IsAny<Severity>(), It.IsAny<Action<SnackbarOptions>?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Announcing_a_change_with_nobody_listening_completes()
    {
        var changes = new DataChangeNotifier();

        await changes.NotifyTransactionsChangedAsync();
        await changes.NotifyGroupsChangedAsync();
    }
}

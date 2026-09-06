using GroupSplit.App.Shared.Services;
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

    // ---- The clients, the presenter and the two states over it -----------------------------

    private readonly Mock<ITransactionsClient> _transactionsClient = new();
    private readonly Mock<IGroupsClient> _groupsClient = new();
    private readonly Mock<ISnackbar> _snackbar = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly TransactionsPageStateService _expensesPage;
    private readonly GroupsPageStateService _groupsPage;

    public PageStateRefreshTest()
    {
        _transactionsClient
            .Setup(c => c.GetTransactionsAsAsyncEnumerable(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => _transactions.Where(t => t.PaidByUserId == Me).ToList().ToAsyncEnumerable());

        _transactionsClient
            .Setup(c => c.CreateTransactionAsync(It.IsAny<CreateTransactionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateTransactionRequest request, CancellationToken _) =>
            {
                var group = _groups.Single(g => g.Id == request.GroupId);
                var created = Expense(group.Id, group.Name, request.Name, request.Amount);
                _transactions.Add(created);
                return created;
            });

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

        _groupsClient
            .Setup(c => c.GetGroupsAsAsyncEnumerable(It.IsAny<CancellationToken>()))
            .Returns(() => _groups.ToList().ToAsyncEnumerable());

        _groupsClient
            .Setup(c => c.GetGroupTransactionsAsAsyncEnumerable(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, DateTimeOffset? _, DateTimeOffset? _, CancellationToken _) =>
                _transactions.Where(t => t.GroupId == id).ToList().ToAsyncEnumerable());

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

        _expensesPage = new TransactionsPageStateService(_transactionsClient.Object, new TransactionsTracker(),
            _snackbar.Object, guard, presenter, _changes);
        _groupsPage = new GroupsPageStateService(new GroupsTracker(), _groupsClient.Object, _snackbar.Object,
            _transactionsClient.Object, guard, presenter, _changes);
    }

    private Task ReadyAsync() => Task.WhenAll(_expensesPage.IsReadyTask, _groupsPage.IsReadyTask);

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
        var dinner = _expensesPage.Transactions.Single(t => t.Name == "Dinner");
        var balanceReadsBefore = BalanceReads;

        var done = await _expensesPage.UpdateAsync(dinner, AmountPatch(120m));

        Assert.True(done);
        Assert.Equal(120m, _expensesPage.Transactions.Single(t => t.Name == "Dinner").Amount);
        Assert.Equal(120m, _groupsPage.Transactions.Single(t => t.Name == "Dinner").Amount);
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
        Assert.Contains(_expensesPage.Transactions, t => t.Name == "Museum");
        Assert.Contains(_groupsPage.Transactions, t => t.Name == "Museum");
        Assert.Equal(150m, _groupsPage.Balance!.NetBalances.Single().Balance);
    }

    [Fact]
    public async Task Deleting_an_expense_removes_it_from_both_pages()
    {
        await ReadyAsync();
        var taxi = _expensesPage.Transactions.Single(t => t.Name == "Taxi");

        var done = await _expensesPage.DeleteAsync(taxi);

        Assert.True(done);
        Assert.DoesNotContain(_expensesPage.Transactions, t => t.Id == taxi.Id);
        Assert.DoesNotContain(_groupsPage.Transactions, t => t.Id == taxi.Id);
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

        Assert.Equal(200m, _expensesPage.Transactions.Single(t => t.Name == "Dinner").Amount);
        Assert.Equal(200m, _groupsPage.Transactions.Single(t => t.Name == "Dinner").Amount);
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
        var expensesBefore = _expensesPage.Transactions;
        var groupBefore = _groupsPage.Transactions;
        var dinner = expensesBefore.Single(t => t.Name == "Dinner");

        await _expensesPage.UpdateAsync(dinner, AmountPatch(1m));

        Assert.NotSame(expensesBefore, _expensesPage.Transactions);
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
        Assert.Empty(_groupsPage.Transactions);
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
        Assert.All(_expensesPage.Transactions.Where(t => t.GroupId == Trip),
            t => Assert.Equal("Lisbon 2026", t.GroupName));
    }

    [Fact]
    public async Task A_group_change_keeps_the_selection_by_id_across_the_new_list()
    {
        await ReadyAsync();
        _groupsPage.SelectedGroup = _groupsPage.Groups.Single(g => g.Id == Flat);

        await _changes.NotifyGroupsChangedAsync();

        Assert.Equal(Flat, _groupsPage.SelectedGroup?.Id);
        Assert.Single(_groupsPage.Transactions);
        Assert.Equal(40m, _groupsPage.Balance!.NetBalances.Single().Balance);
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
        var dinner = _expensesPage.Transactions.Single(t => t.Name == "Dinner");
        _transactionsClient
            .Setup(c => c.GetTransactionsAsAsyncEnumerable(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => throw new ApiException("down", 503, "", new Dictionary<string, IEnumerable<string>>(), null));

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

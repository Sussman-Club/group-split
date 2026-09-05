using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Groups;

public class GroupsPageStateService : IGroupsPageStateService
{
    private readonly GroupsTracker _tracker;
    private readonly IGroupsClient _groupsClient;
    private readonly ISnackbar _snackbar;
    private readonly ITransactionsClient _transactionsClient;
    private readonly LoadGuard _guard;
    private readonly ApiErrorPresenter _errors;
    private readonly DataChangeNotifier _changes;

    public Task IsReadyTask { get; }
    public bool IsLoading { get; private set; }
    private Task _selectedLoad = Task.CompletedTask;

    /// <summary>
    /// The group to land on after the next reload of the list, when it is not the one
    /// selected now: the group that was just created.
    /// </summary>
    private Guid? _selectOnReload;

    public GroupsPageStateService(GroupsTracker tracker, IGroupsClient groupsClient, ISnackbar snackbar,
        ITransactionsClient transactionsClient, LoadGuard guard, ApiErrorPresenter errors,
        DataChangeNotifier changes)
    {
        _tracker = tracker;
        _groupsClient = groupsClient;
        _snackbar = snackbar;
        _transactionsClient = transactionsClient;
        _guard = guard;
        _errors = errors;
        _changes = changes;

        // Everything here is a copy of the server's, re-read when a write says it changed,
        // whichever page the write was made from. A group change reloads the list and,
        // through the selection, the selected group's figures; an expense change reloads
        // just those figures, which is all an expense can move.
        _changes.GroupsChanged += RefreshGroupsAsync;
        _changes.TransactionsChanged += RefreshSelectedAsync;

        IsReadyTask = Task.Run(async () =>
        {
            if (tracker.Groups is not null) return;
            await RefreshGroupsAsync();
        });
    }

    public ICollection<GroupResponse> Groups
    {
        get => _tracker.Groups ?? [];
        private set
        {
            _tracker.Groups = value;
            OnGroupsChanged?.Invoke();
        }
    }

    public GroupResponse? SelectedGroup
    {
        get => _tracker.SelectedGroup;
        set
        {
            if (value is not null && Groups.All(g => g.Id != value.Id))
                throw new ArgumentException("The selected group must be part of the user's groups.", nameof(value));

            _tracker.SelectedGroup = value;
            OnGroupSelected?.Invoke();
            _selectedLoad = LoadSelectedAsync();
        }
    }

    public ICollection<TransactionResponse> Transactions
    {
        get => _tracker.Transactions;
        private set
        {
            _tracker.Transactions = value;
            OnTransactionsChanged?.Invoke();
        }
    }

    public UserGroupBalanceResponse? Balance
    {
        get => _tracker.Balance;
        private set
        {
            _tracker.Balance = value;
            OnTransactionsChanged?.Invoke();
        }
    }

    public event Action? OnGroupSelected;
    public event Action? OnTransactionsChanged;
    public event Action? OnGroupsChanged;

    /// <summary>
    /// Re-reads the list of groups, then the selected group through it. Complete once both
    /// halves have landed, so a caller that awaits it sees the whole picture.
    /// </summary>
    private async Task RefreshGroupsAsync()
    {
        await _guard.RunAsync(() => LoadGroupsAsync(), "your groups");
        await _selectedLoad;
    }

    /// <summary>
    /// Re-reads the selected group's expenses and balances in place. Unlike a selection,
    /// this does not raise the loading flag: what is on screen is the right group, only a
    /// moment behind, and dimming it would read as a change of subject.
    /// </summary>
    private Task RefreshSelectedAsync() =>
        _guard.RunAsync(async () =>
        {
            await LoadTransactionsAsync();
            await LoadGroupBalancesAsync();
        }, "this group");

    // Both halves of the selected group travel together, under one loading
    // flag, and a failure clears them: stale figures under a fresh name read
    // as the wrong answer, an empty panel reads as "not loaded".
    private async Task LoadSelectedAsync()
    {
        IsLoading = true;
        OnTransactionsChanged?.Invoke();

        var loaded = await _guard.RunAsync(async () =>
        {
            await LoadTransactionsAsync();
            await LoadGroupBalancesAsync();
        }, "this group");

        if (!loaded)
        {
            _tracker.Transactions = [];
            _tracker.Balance = null;
        }

        IsLoading = false;
        OnTransactionsChanged?.Invoke();
    }

    // The loads below always assign a new collection, never edit the old one in place: a
    // component that was handed the previous one only looks again when the reference
    // changes.

    private async Task LoadTransactionsAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
        {
            Transactions = [];
        }
        else
        {
            Transactions = await _groupsClient
                .GetGroupTransactionsAsAsyncEnumerable(SelectedGroup.Id, cancellationToken: cancellationToken)
                .OrderByDescending(t => t.DateTime)
                .ToListAsync(cancellationToken);
        }
    }

    private async Task LoadGroupsAsync(CancellationToken cancellationToken = default)
    {
        Groups = await _groupsClient
            .GetGroupsAsAsyncEnumerable(cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);

        // Stay on the group that was selected -- by id, since the list is new objects --
        // unless a write asked for another one. The setter re-reads the group's figures.
        var wanted = _selectOnReload ?? SelectedGroup?.Id;
        _selectOnReload = null;

        SelectedGroup = Groups.FirstOrDefault(g => g.Id == wanted) ??
                        Groups.FirstOrDefault();
    }

    private async Task LoadGroupBalancesAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
        {
            Balance = null;
        }
        else
        {
            Balance = await _groupsClient.GetGroupUserBalanceAsync(SelectedGroup.Id, cancellationToken: cancellationToken);
        }
    }

    // Every write below runs through the presenter: a refusal from the API becomes an
    // error snackbar naming the reason, a lost session becomes a sign-in, and the caller
    // gets false instead of an exception it would have had to catch itself. Once the write
    // has landed it is announced rather than applied here by hand, so this page's copy and
    // the expenses page's are re-read from the same source and cannot drift apart.

    public Task<bool> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default) =>
        _errors.TryAsync(async () =>
        {
            var newGroup = await _groupsClient.CreateGroupAsync(request, cancellationToken);
            _snackbar.Add("Group created successfully.", Severity.Success);

            _selectOnReload = newGroup.Id;
            await _changes.NotifyGroupsChangedAsync();
        }, "Could not create the group.");

    public IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null) return AsyncEnumerable.Empty<UserInfo>();

        return _groupsClient.GetGroupMembersAsAsyncEnumerable(SelectedGroup.Id, cancellationToken);
    }

    public Task<bool> AddGroupMemberAsync(AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            await _groupsClient.AddGroupMemberAsync(SelectedGroup.Id, request, cancellationToken);
            _snackbar.Add("Member added successfully.", Severity.Success);
            await _changes.NotifyGroupsChangedAsync();
        }, "Could not add the member.");
    }

    public Task<bool> RemoveGroupMemberAsync(Guid memberUserId, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            await _groupsClient.RemoveGroupMemberAsync(SelectedGroup.Id, memberUserId, cancellationToken);
            _snackbar.Add("Member removed successfully.", Severity.Success);
            await _changes.NotifyGroupsChangedAsync();
        }, "Could not remove the member.");
    }

    public Task<bool> UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            await _groupsClient.UpdateGroupAsync(SelectedGroup.Id, updateRequest, cancellationToken);
            _snackbar.Add("Group updated successfully.", Severity.Success);
            await _changes.NotifyGroupsChangedAsync();
        }, "Could not update the group.");
    }

    public Task<bool> CreateTransactionAsync(CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            await _transactionsClient.CreateTransactionAsync(request, cancellationToken);
            _snackbar.Add("Transaction created successfully.", Severity.Success);
            await _changes.NotifyTransactionsChangedAsync();
        }, "Could not save the expense.");
    }

    public Task<bool> SettleAsync(SettleRequest request, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            await _groupsClient.SettleGroupDebtsAsync(SelectedGroup.Id, request, cancellationToken);
            _snackbar.Add("Group debts settled successfully.", Severity.Success);
            await _changes.NotifyTransactionsChangedAsync();
        }, "Could not record the settlement.");
    }
}

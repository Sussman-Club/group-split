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

    public Task IsReadyTask { get; }
    public bool IsLoading { get; private set; }
    private Task _transactionsLoad = Task.CompletedTask;
    private Task _balancesLoad = Task.CompletedTask;

    public GroupsPageStateService(GroupsTracker tracker, IGroupsClient groupsClient, ISnackbar snackbar,
        ITransactionsClient transactionsClient, LoadGuard guard, ApiErrorPresenter errors)
    {
        _tracker = tracker;
        _groupsClient = groupsClient;
        _snackbar = snackbar;
        _transactionsClient = transactionsClient;
        _guard = guard;
        _errors = errors;
        IsReadyTask = Task.Run(async () =>
        {
            if (tracker.Groups is not null) return;
            await _guard.RunAsync(() => LoadGroupsAsync(), "your groups");
            await _transactionsLoad;
            await _balancesLoad;
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
            _transactionsLoad = LoadSelectedAsync();
            _balancesLoad = _transactionsLoad;
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

        SelectedGroup = Groups.FirstOrDefault(g => g.Id == SelectedGroup?.Id) ??
                        Groups.FirstOrDefault();
    }

    public async Task LoadGroupBalancesAsync(CancellationToken cancellationToken = default)
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
    // gets false instead of an exception it would have had to catch itself.

    public Task<bool> CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default) =>
        _errors.TryAsync(async () =>
        {
            var newGroup = await _groupsClient.CreateGroupAsync(request, cancellationToken);
            Groups.Add(newGroup);
            SelectedGroup = newGroup;
            _snackbar.Add("Group created successfully.", Severity.Success);
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
            var response = await _groupsClient.AddGroupMemberAsync(SelectedGroup.Id, request, cancellationToken);

            UpdateSelectedGroup(response);
            _snackbar.Add("Member added successfully.", Severity.Success);
        }, "Could not add the member.");
    }

    public Task<bool> RemoveGroupMemberAsync(Guid memberUserId, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            var response = await _groupsClient.RemoveGroupMemberAsync(SelectedGroup.Id, memberUserId, cancellationToken);

            UpdateSelectedGroup(response);
            _snackbar.Add("Member removed successfully.", Severity.Success);
        }, "Could not remove the member.");
    }

    public Task<bool> UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            var updatedGroup = await _groupsClient.UpdateGroupAsync(SelectedGroup.Id, updateRequest, cancellationToken);
            UpdateSelectedGroup(updatedGroup);

            _snackbar.Add("Group updated successfully.", Severity.Success);
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

            await LoadTransactionsAsync(cancellationToken);
            await LoadGroupBalancesAsync(cancellationToken);
            _snackbar.Add("Transaction created successfully.", Severity.Success);
        }, "Could not save the expense.");
    }

    public Task<bool> SettleAsync(SettleRequest request, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        return _errors.TryAsync(async () =>
        {
            await _groupsClient.SettleGroupDebtsAsync(SelectedGroup.Id, request, cancellationToken);

            await LoadTransactionsAsync(cancellationToken);
            await LoadGroupBalancesAsync(cancellationToken);
            _snackbar.Add("Group debts settled successfully.", Severity.Success);
        }, "Could not record the settlement.");
    }

    private void UpdateSelectedGroup(GroupResponse group)
    {
        var groupList = Groups as List<GroupResponse> ?? Groups.ToList();
        var index = groupList.FindIndex(g => g.Id == group.Id);

        if (index < 0) return;

        groupList[index] = group;
        Groups = groupList;
        SelectedGroup = group;
    }
}

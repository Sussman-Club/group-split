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

    public Task IsReadyTask { get; }
    private Task _transactionsLoad = Task.CompletedTask;
    private Task _balancesLoad = Task.CompletedTask;

    public GroupsPageStateService(GroupsTracker tracker, IGroupsClient groupsClient, ISnackbar snackbar,
        ITransactionsClient transactionsClient)
    {
        _tracker = tracker;
        _groupsClient = groupsClient;
        _snackbar = snackbar;
        _transactionsClient = transactionsClient;
        IsReadyTask = Task.Run(async () =>
        {
            if (tracker.Groups is not null) return;
            await LoadGroupsAsync();
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
            _transactionsLoad = LoadTransactionsAsync();
            _balancesLoad = LoadGroupBalancesAsync();
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

    public async Task CreateGroupAsync(CreateGroupRequest request, CancellationToken cancellationToken = default)
    {
        var newGroup = await _groupsClient.CreateGroupAsync(request, cancellationToken);
        Groups.Add(newGroup);
        SelectedGroup = newGroup;
        _snackbar.Add("Group created successfully.", Severity.Success);
    }

    public IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null) return AsyncEnumerable.Empty<UserInfo>();

        return _groupsClient.GetGroupMembersAsAsyncEnumerable(SelectedGroup.Id, cancellationToken);
    }

    public async Task AddGroupMemberAsync(AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        var response = await _groupsClient.AddGroupMemberAsync(SelectedGroup.Id, request, cancellationToken);

        UpdateSelectedGroup(response);
        _snackbar.Add("Member added successfully.", Severity.Success);
    }

    public async Task RemoveGroupMemberAsync(Guid memberUserId, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        var response = await _groupsClient.RemoveGroupMemberAsync(SelectedGroup.Id, memberUserId, cancellationToken);

        UpdateSelectedGroup(response);
        _snackbar.Add("Member removed successfully.", Severity.Success);
    }

    public async Task UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        await _groupsClient.UpdateGroupAsync(SelectedGroup.Id, updateRequest, cancellationToken);

        // TODO: Update the selected group in the UI
        var updatedGroup = await _groupsClient.GetGroupAsync(SelectedGroup.Id, cancellationToken);
        UpdateSelectedGroup(updatedGroup);

        _snackbar.Add("Group updated successfully.", Severity.Success);
    }

    public async Task CreateTransactionAsync(CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");

        await _transactionsClient.CreateTransactionAsync(request, cancellationToken);

        await LoadTransactionsAsync(cancellationToken);
        await LoadGroupBalancesAsync(cancellationToken);
        _snackbar.Add("Transaction created successfully.", Severity.Success);
    }

    public async Task SettleAsync(SettleRequest request, CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null)
            throw new InvalidOperationException("No group is selected.");
        
        await _groupsClient.SettleGroupDebtsAsync(SelectedGroup.Id, request, cancellationToken);
        
        await LoadTransactionsAsync(cancellationToken);
        await LoadGroupBalancesAsync(cancellationToken);
        _snackbar.Add("Group debts settled successfully.", Severity.Success);
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
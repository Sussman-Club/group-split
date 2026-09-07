using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Groups;

public class GroupsPageStateService : IGroupsPageStateService
{
    /// <summary>
    /// How many of the selected group's expenses to read. The card on the group page shows
    /// this many, so a larger page would be rows fetched and thrown away, and a smaller one
    /// would leave a gap; the header's count comes from the page's total, not its length.
    /// </summary>
    public const int RecentPageSize = 8;

    private readonly GroupsTracker _tracker;
    private readonly IGroupsClient _groupsClient;
    private readonly IUsersClient _usersClient;
    private readonly LoadGuard _guard;
    private readonly ApiErrorPresenter _errors;
    private readonly DataChangeNotifier _changes;
    private readonly IGroupCommands _groupCommands;
    private readonly ITransactionCommands _transactionCommands;

    public Task IsReadyTask { get; }
    public bool IsLoading { get; private set; }
    private Task _selectedLoad = Task.CompletedTask;

    public GroupsPageStateService(GroupsTracker tracker, IGroupsClient groupsClient,
        IUsersClient usersClient, LoadGuard guard, ApiErrorPresenter errors,
        DataChangeNotifier changes, IGroupCommands groupCommands,
        ITransactionCommands transactionCommands)
    {
        _tracker = tracker;
        _groupsClient = groupsClient;
        _usersClient = usersClient;
        _guard = guard;
        _errors = errors;
        _changes = changes;
        _groupCommands = groupCommands;
        _transactionCommands = transactionCommands;

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

    public PagedResponse<TransactionResponse>? Transactions
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

    public UserPositionResponse? Position
    {
        get => _tracker.Position;
        private set
        {
            _tracker.Position = value;
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
        await _guard.RunAsync(async () =>
        {
            await LoadGroupsAsync();
            await LoadPositionAsync();
        }, "your groups");

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
            await LoadPositionAsync();
        }, "this group");

    /// <summary>
    /// The cross-group position. Read alongside the selected group rather than with it: an
    /// expense in any group moves it, and it is what the home page and the group cards
    /// show, neither of which has a group selected.
    /// </summary>
    private async Task LoadPositionAsync(CancellationToken cancellationToken = default)
    {
        Position = await _usersClient.GetCurrentUserPositionAsync(cancellationToken);
    }

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
            _tracker.Transactions = null;
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
            Transactions = null;
        }
        else
        {
            // The newest few, which is exactly what the card on the page shows, plus the
            // count of all of them for the header. Ordering is the server's now: it is the
            // only end that can order rows it did not send.
            Transactions = await _groupsClient.GetGroupTransactionsAsync(
                SelectedGroup.Id,
                sortBy: TransactionQuery.DefaultSortBy,
                sortDescending: true,
                page: 1,
                pageSize: RecentPageSize,
                cancellationToken: cancellationToken);
        }
    }

    private async Task LoadGroupsAsync(CancellationToken cancellationToken = default)
    {
        Groups = await _groupsClient
            .GetGroupsAsAsyncEnumerable(cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);

        // Stay on the group that was selected -- by id, since the list is new objects.
        // The setter re-reads the group's figures.
        var wanted = SelectedGroup?.Id;

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

    public async Task<bool> CreateGroupAsync(CreateGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var created = await _groupCommands.CreateAsync(request, cancellationToken);

        if (created is null) return false;

        // Landing on the new group is this class's business rather than the command's,
        // which is why the command hands the group back instead of doing anything with it.
        // The announcement it already made reloaded the list, so the group is in hand and
        // selecting it costs nothing -- a second notify here would re-read the whole list
        // to learn what this line already knows.
        if (Groups.FirstOrDefault(group => group.Id == created.Id) is { } landed)
            SelectedGroup = landed;

        return true;
    }

    public IAsyncEnumerable<UserInfo> GetGroupMembersAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup is null) return AsyncEnumerable.Empty<UserInfo>();

        return _groupsClient.GetGroupMembersAsAsyncEnumerable(SelectedGroup.Id, cancellationToken);
    }

    public Task<bool> InviteToGroupAsync(AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        var group = Selected();

        return _groupCommands.InviteAsync(group.Id, group.Name, request, cancellationToken);
    }

    public Task<IReadOnlyList<GroupInvitationResponse>> GetGroupInvitationsAsync(
        CancellationToken cancellationToken = default) =>
        SelectedGroup is null
            ? Task.FromResult<IReadOnlyList<GroupInvitationResponse>>([])
            : ReadInvitationsAsync(SelectedGroup.Id, cancellationToken);

    private async Task<IReadOnlyList<GroupInvitationResponse>> ReadInvitationsAsync(Guid groupId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GroupInvitationResponse> pending = [];

        await _errors.TryAsync(async () =>
                pending = [.. await _groupsClient.GetGroupInvitationsAsync(groupId, cancellationToken)],
            "Could not load the invitations.");

        return pending;
    }

    public Task<bool> WithdrawInvitationAsync(Guid invitationId, string email,
        CancellationToken cancellationToken = default) =>
        _groupCommands.WithdrawInvitationAsync(Selected().Id, invitationId, email, cancellationToken);

    public Task<GroupJoinLinkResponse?> GetGroupJoinLinkAsync(CancellationToken cancellationToken = default) =>
        SelectedGroup is null
            ? Task.FromResult<GroupJoinLinkResponse?>(null)
            : ReadJoinLinkAsync(SelectedGroup.Id, cancellationToken);

    private async Task<GroupJoinLinkResponse?> ReadJoinLinkAsync(Guid groupId,
        CancellationToken cancellationToken)
    {
        GroupJoinLinkResponse? link = null;

        await _errors.TryAsync(async () =>
                link = (await _groupsClient.GetGroupJoinLinksAsync(groupId, cancellationToken))
                    .OrderByDescending(candidate => candidate.CreatedAt)
                    .FirstOrDefault(),
            "Could not load the join link.");

        return link;
    }

    public Task<GroupJoinLinkResponse?> CreateGroupJoinLinkAsync(CancellationToken cancellationToken = default)
    {
        var group = Selected();

        return _groupCommands.CreateJoinLinkAsync(group.Id, group.Name, cancellationToken);
    }

    public Task<bool> RevokeGroupJoinLinkAsync(CancellationToken cancellationToken = default)
    {
        var group = Selected();

        return _groupCommands.RevokeJoinLinkAsync(group.Id, group.Name, cancellationToken);
    }

    public Task<bool> RemoveGroupMemberAsync(Guid memberUserId, string memberName,
        CancellationToken cancellationToken = default) =>
        _groupCommands.RemoveMemberAsync(Selected().Id, memberUserId, memberName, cancellationToken);

    public Task<bool> LeaveGroupAsync(CancellationToken cancellationToken = default)
    {
        var group = Selected();

        return _groupCommands.LeaveAsync(group.Id, group.Name, cancellationToken);
    }

    public Task<bool> UpdateGroupAsync(JsonPatchDocument<CreateGroupRequest> updateRequest,
        CancellationToken cancellationToken = default) =>
        _groupCommands.RenameAsync(Selected().Id, updateRequest, cancellationToken);

    public Task<bool> CreateTransactionAsync(CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        Selected();

        return _transactionCommands.CreateAsync(request, cancellationToken);
    }

    /// <summary>
    /// The selected group, or a failure that names the mistake. Every write below is about
    /// a group, so none of them means anything with none selected -- and a null reference
    /// two frames deeper would say much less about why.
    /// </summary>
    private GroupResponse Selected() =>
        SelectedGroup ?? throw new InvalidOperationException("No group is selected.");

    public Task<bool> ArchiveGroupAsync(CancellationToken cancellationToken = default) =>
        SetArchivedAsync(archived: true, cancellationToken);

    public Task<bool> UnarchiveGroupAsync(CancellationToken cancellationToken = default) =>
        SetArchivedAsync(archived: false, cancellationToken);

    /// <summary>
    /// Both directions. Archiving is this person's own view of the group -- it hides it
    /// from their list and touches nothing else -- so the reload that follows re-selects by
    /// id and the flag arrives the way every other change to a group does.
    /// </summary>
    private Task<bool> SetArchivedAsync(bool archived, CancellationToken cancellationToken)
    {
        var group = Selected();

        return _groupCommands.SetArchivedAsync(group.Id, group.Name, archived, cancellationToken);
    }

    public Task<bool> SettleAsync(SettleRequest request, string otherName,
        CancellationToken cancellationToken = default) =>
        _groupCommands.SettleAsync(Selected().Id, request, otherName, cancellationToken);

    public Task<SettleUpResponse?> SettleUpAsync(SettleUpRequest request,
        CancellationToken cancellationToken = default) =>
        _groupCommands.SettleUpAsync(Selected().Id, request, cancellationToken);
}

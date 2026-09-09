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

    /// <summary>
    /// The load behind the selection in force: the figures' journey from the server, for
    /// anybody who needs to wait for them. Complete once they have landed, or failed.
    /// </summary>
    private Task _selectedLoad = Task.CompletedTask;

    /// <summary>
    /// Counts selections. Every read of the selected group's figures remembers the number
    /// it was started under and writes nothing down if the number has moved on, which is
    /// what keeps a slow answer about the previous group from landing on the current one.
    /// </summary>
    /// <remarks>
    /// The selection changes faster than the server answers: somebody opening one group
    /// from the list and then another has two reads in flight, and nothing about the
    /// network promises the second lands last. Without this the first group's expenses and
    /// balances arrived after the second's and were written down as its own.
    /// </remarks>
    private int _selectionVersion;

    /// <summary>Whether the load under way is one that dims the page: a change of subject, not a refresh.</summary>
    private bool _selecting;

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

        // Started here rather than on a pool thread: the events this raises are what
        // components re-render on, and under interactive server rendering those have to
        // reach the circuit's own thread. A first read that has already landed -- the
        // prerender persisted it -- is not read again.
        IsReadyTask = tracker.Groups is not null ? Task.CompletedTask : RefreshGroupsAsync();
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

            var changed = value?.Id != _tracker.SelectedGroup?.Id;

            _tracker.SelectedGroup = value;

            if (changed)
            {
                // A different group. What is held describes the one being left, and a
                // page that opened on this one with the old figures under its name was the
                // wrong answer for as long as the server took -- so they go now, and the
                // page shows nothing rather than somebody else's balances.
                ClearFigures();
                _selectedLoad = LoadSelectedAsync(value, ++_selectionVersion);
            }
            else if (value is not null)
            {
                // The same group, handed over again -- the list was re-read and this is
                // its new object. The figures are still its own; they are re-read in place,
                // without the dimming a change of subject gets.
                _selectedLoad = RefreshFiguresAsync(value, _selectionVersion);
            }

            OnGroupSelected?.Invoke();
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

    public bool IsLoading => _selecting && !_selectedLoad.IsCompleted;

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
    /// Re-reads the selected group's expenses and balances in place, and the position with
    /// them. Unlike a selection, this does not raise the loading flag: what is on screen is
    /// the right group, only a moment behind, and dimming it would read as a change of
    /// subject.
    /// </summary>
    private async Task RefreshSelectedAsync()
    {
        _selectedLoad = RefreshFiguresAsync(SelectedGroup, _selectionVersion);

        await _selectedLoad;
        await _guard.RunAsync(() => LoadPositionAsync(), "this group");
    }

    public Task EnsureSelectedLoadedAsync()
    {
        if (SelectedGroup is not { } selected)
            return Task.CompletedTask;

        // Either the figures are its own, or they are on their way. A tracker restored from
        // the prerender can say neither -- the selection persisted before its figures
        // landed, or beside the previous group's -- and that is the case this exists for.
        if (_tracker.FiguresGroupId == selected.Id || !_selectedLoad.IsCompleted)
            return _selectedLoad;

        return _selectedLoad = LoadSelectedAsync(selected, ++_selectionVersion);
    }

    /// <summary>
    /// The cross-group position. Read alongside the selected group rather than with it: an
    /// expense in any group moves it, and it is what the home page and the group cards
    /// show, neither of which has a group selected.
    /// </summary>
    private async Task LoadPositionAsync(CancellationToken cancellationToken = default)
    {
        Position = await _usersClient.GetCurrentUserPositionAsync(cancellationToken);
    }

    /// <summary>
    /// The figures behind a change of subject: both halves together, under the loading
    /// flag, and a failure leaves them clear -- stale figures under a fresh name read as
    /// the wrong answer, an empty panel reads as "not loaded".
    /// </summary>
    private async Task LoadSelectedAsync(GroupResponse? group, int version)
    {
        _selecting = true;
        OnTransactionsChanged?.Invoke();

        try
        {
            var loaded = await RefreshFiguresAsync(group, version);

            // Nothing landed, and nothing of this group's was there before. The panel
            // stays empty rather than showing the group that was left.
            if (!loaded && version == _selectionVersion)
            {
                ClearFigures();
                OnTransactionsChanged?.Invoke();
            }
        }
        finally
        {
            if (version == _selectionVersion)
                _selecting = false;

            OnTransactionsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Reads one group's expenses and balances and writes them down -- if, once they
    /// arrive, that group is still the one selected. An answer about a group nobody is
    /// looking at any more is dropped whole: half of it landing would be worse than none.
    /// A failure writes nothing down either: on a refresh what is on screen is the right
    /// group's, only a moment behind, and the failure has already been shown.
    /// </summary>
    /// <returns>Whether the figures landed.</returns>
    private async Task<bool> RefreshFiguresAsync(GroupResponse? group, int version)
    {
        if (group is null)
        {
            ClearFigures();
            OnTransactionsChanged?.Invoke();
            return true;
        }

        PagedResponse<TransactionResponse>? transactions = null;
        UserGroupBalanceResponse? balance = null;

        var loaded = await _guard.RunAsync(async () =>
        {
            // The newest few, which is exactly what the card on the page shows, plus the
            // count of all of them for the header. Ordering is the server's now: it is the
            // only end that can order rows it did not send.
            transactions = await _groupsClient.GetGroupTransactionsAsync(
                group.Id,
                sortBy: TransactionQuery.DefaultSortBy,
                sortDescending: true,
                page: 1,
                pageSize: RecentPageSize);

            balance = await _groupsClient.GetGroupUserBalanceAsync(group.Id);
        }, "this group");

        if (version != _selectionVersion || !loaded)
            return loaded;

        // Both assign a new object rather than editing the old one in place: a component
        // that was handed the previous one only looks again when the reference changes.
        _tracker.FiguresGroupId = group.Id;
        Transactions = transactions;
        Balance = balance;

        return true;
    }

    private void ClearFigures()
    {
        _tracker.Transactions = null;
        _tracker.Balance = null;
        _tracker.FiguresGroupId = null;
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
        {
            SelectedGroup = landed;
            await EnsureSelectedLoadedAsync();
        }

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

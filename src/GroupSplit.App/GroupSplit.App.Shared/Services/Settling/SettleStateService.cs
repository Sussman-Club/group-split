using GroupSplit.App.Shared.Extensions;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.Shared;
using MudBlazor;

namespace GroupSplit.App.Shared.Services.Settling;

/// <inheritdoc cref="ISettleStateService"/>
/// <remarks>
/// Follows the shape the other page states have: it subscribes to the notifier, re-reads
/// what it holds when a write announces itself, and assigns a new object each time rather
/// than editing one in place.
/// <para>
/// The plan is read as soon as somebody signed in asks for it, because the nav badge is on
/// every page. The history waits until the Settle page opens, because nothing else needs
/// it.
/// </para>
/// </remarks>
public sealed class SettleStateService : ISettleStateService, IDisposable
{
    /// <summary>
    /// How much history the page shows before somebody asks for more. Enough that "did I
    /// already pay this?" is usually answered without a second read.
    /// </summary>
    private const int HistoryPageSize = 10;

    private readonly IUsersClient _users;
    private readonly SettleTracker _tracker;
    private readonly LoadGuard _guard;
    private readonly DataChangeNotifier _changes;
    private readonly ApiErrorPresenter _errors;
    private readonly ISnackbar _snackbar;

    private readonly Lock _lock = new();

    private Task? _loaded;
    private bool _historyWanted;
    private int _historyShown = HistoryPageSize;

    public SettleStateService(
        IUsersClient users,
        SettleTracker tracker,
        LoadGuard guard,
        DataChangeNotifier changes,
        ApiErrorPresenter errors,
        ISnackbar snackbar)
    {
        _users = users;
        _tracker = tracker;
        _guard = guard;
        _changes = changes;
        _errors = errors;
        _snackbar = snackbar;

        // The plan is a view over every balance in every group, so anything that moves one
        // moves it: an expense recorded anywhere, a settlement, a member joining or leaving.
        _changes.TransactionsChanged += RefreshAsync;
        _changes.GroupsChanged += RefreshAsync;
    }

    public SettlementPlanResponse? Plan
    {
        get => _tracker.Plan;
        private set => _tracker.Plan = value;
    }

    public PagedResponse<SettlementResponse>? History
    {
        get => _tracker.History;
        private set => _tracker.History = value;
    }

    public int OutstandingCount => Plan is { } plan ? plan.YouPay.Count + plan.OwedToYou.Count : 0;

    public bool IsLoading { get; private set; }

    public event Action? OnChanged;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Prerendering may already have left a plan in the tracker. Nothing to fetch,
            // and nothing to await either.
            if (_loaded is null && Plan is not null)
                _loaded = Task.CompletedTask;

            _loaded ??= ReadAsync(cancellationToken);

            return _loaded;
        }
    }

    public Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        _historyWanted = true;

        return History is not null ? Task.CompletedTask : ReadHistoryAsync(cancellationToken);
    }

    public async Task ShowMoreHistoryAsync(CancellationToken cancellationToken = default)
    {
        // One page that grows rather than a pager: a history is read in order, and a page
        // two that hides page one breaks the reading.
        _historyShown += HistoryPageSize;

        await ReadHistoryAsync(cancellationToken);
    }

    public async Task RefreshAsync()
    {
        // Nothing has asked for any of this yet, so there is nothing to bring up to date.
        // Reading here would be a request on behalf of a page nobody is looking at.
        if (_loaded is null)
            return;

        await ReadAsync(CancellationToken.None);

        if (_historyWanted)
            await ReadHistoryAsync(CancellationToken.None);
    }

    public async Task<SettleWithPersonResponse?> SettleAsync(SettleWithPersonRequest request,
        string personName, CancellationToken cancellationToken = default)
    {
        SettleWithPersonResponse? recorded = null;

        var done = await _errors.TryAsync(async () =>
        {
            recorded = await _users.SettleWithPersonAsync(request, cancellationToken);

            // Names the groups it landed in when there is more than one. The whole promise
            // of this screen is that a payment spanning two groups is one action, and the
            // confirmation is where that promise is either kept or left unverifiable.
            var where = recorded.Groups.Count switch
            {
                > 1 => $" across {string.Join(" and ", recorded.Groups.Select(part => part.GroupName))}",
                1 => $" in {recorded.Groups[0].GroupName}",
                _ => ""
            };

            _snackbar.Add(
                request.Direction is SettlementDirection.YouPaidThem
                    ? $"Recorded {recorded.Amount.ToMoney()} paid to {personName}{where}."
                    : $"Recorded {recorded.Amount.ToMoney()} from {personName}{where}.",
                Severity.Success);

            // Balances everywhere have moved, so this goes out to every state that holds
            // one -- including this one, through the handler wired in the constructor.
            await _changes.NotifyTransactionsChangedAsync();
        }, $"Could not record the payment with {personName}.");

        return done ? recorded : null;
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        Announce();

        await _guard.RunAsync(async () =>
                Plan = await _users.GetSettlementPlanAsync(cancellationToken),
            "your settlement plan");

        IsLoading = false;
        Announce();
    }

    private async Task ReadHistoryAsync(CancellationToken cancellationToken)
    {
        await _guard.RunAsync(async () =>
                History = await _users.GetSettlementsAsync(page: 1, pageSize: _historyShown,
                    cancellationToken: cancellationToken),
            "your settlements");

        Announce();
    }

    private void Announce() => OnChanged?.Invoke();

    public void Dispose()
    {
        _changes.TransactionsChanged -= RefreshAsync;
        _changes.GroupsChanged -= RefreshAsync;
    }
}

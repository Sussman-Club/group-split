using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Commands;
using GroupSplit.App.Shared.Services.Errors;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.State;

/// <summary>
/// What the group page shows while the group it shows is changing.
/// </summary>
/// <remarks>
/// The reported symptom was one group's balances and recent expenses under another
/// group's name. Three things produced it, and each is pinned here. A selection started a
/// read and nothing checked, when the read answered, that the group was still the one
/// selected -- so opening two groups in quick succession could land the first group's
/// figures last. The figures of the group being left stayed on screen until the new
/// group's arrived, which on a slow connection was long enough to read them as the
/// answer. And the prerender persisted the selection and the figures separately, so the
/// interactive side could be handed a selection matching the URL beside figures that
/// belonged to the group selected before it, and take the match as proof.
/// </remarks>
public class GroupSelectionTest
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly GroupResponse Flat = new(Guid.NewGuid(), "Flat", 2);
    private static readonly GroupResponse Trip = new(Guid.NewGuid(), "Weekend in Lisbon", 3);

    /// <summary>Held open so the trip's answer can be made to arrive when the test says.</summary>
    private readonly TaskCompletionSource _tripAnswers = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the trip's reads wait on <see cref="_tripAnswers"/>. Off until a test arms it.</summary>
    private bool _tripIsSlow;

    private readonly Mock<IGroupsClient> _groupsClient = new();
    private readonly Mock<IUsersClient> _usersClient = new();
    private readonly DataChangeNotifier _changes = new();
    private readonly LoadGuard _guard;

    public GroupSelectionTest()
    {
        // The flat first, so the first read -- which selects the first group and waits for
        // its figures -- is never the one a test wants to hold open.
        _groupsClient
            .Setup(c => c.GetGroupsAsAsyncEnumerable(It.IsAny<CancellationToken>()))
            .Returns(() => new[] { Flat, Trip }.ToAsyncEnumerable());

        _groupsClient
            .Setup(c => c.GetGroupTransactionsAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, DateTimeOffset? _, DateTimeOffset? _, Guid? _, Guid? _, string? _,
                bool? _, string? _, string? _, bool? _, int? _, int? _, CancellationToken _) =>
            {
                await WaitIfSlowAsync(id);
                return PageOf(id);
            });

        _groupsClient
            .Setup(c => c.GetGroupUserBalanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid id, CancellationToken _) =>
            {
                await WaitIfSlowAsync(id);
                return BalanceOf(id);
            });

        _usersClient
            .Setup(c => c.GetCurrentUserPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new UserPositionResponse(0m, 0m, 0m, []));

        var presenter = new ApiErrorPresenter(Mock.Of<IAuthService>(), new Mock<NavigationManager>().Object,
            Mock.Of<ISnackbar>());

        _guard = new LoadGuard(presenter);
    }

    private GroupsPageStateService Build(GroupsTracker? tracker = null) =>
        new(tracker ?? new GroupsTracker(), _groupsClient.Object, _usersClient.Object, _guard,
            new ApiErrorPresenter(Mock.Of<IAuthService>(), new Mock<NavigationManager>().Object,
                Mock.Of<ISnackbar>()),
            _changes, Mock.Of<IGroupCommands>(), Mock.Of<ITransactionCommands>());

    private Task WaitIfSlowAsync(Guid groupId) =>
        _tripIsSlow && groupId == Trip.Id ? _tripAnswers.Task : Task.CompletedTask;

    /// <summary>One expense named after its group, so a page says whose it is.</summary>
    private static PagedResponseOfTransactionResponse PageOf(Guid groupId)
    {
        var group = groupId == Trip.Id ? Trip : Flat;

        var expense = new TransactionResponse
        {
            Id = Guid.NewGuid(),
            Name = $"Dinner in {group.Name}",
            Amount = groupId == Trip.Id ? 96m : 40m,
            DateTime = DateTimeOffset.UtcNow,
            GroupId = group.Id,
            GroupName = group.Name,
            PaidByUserId = Me,
            PaidByUserName = "Me"
        };

        return new PagedResponseOfTransactionResponse([expense], 1, 8, 1);
    }

    private static UserGroupBalanceResponse BalanceOf(Guid groupId) => new()
    {
        NetBalances =
        [
            new GroupNetBalance { UserId = Me, UserName = "Me", Balance = groupId == Trip.Id ? 96m : 40m }
        ]
    };

    private int TransactionReads(Guid groupId) =>
        _groupsClient.Invocations.Count(i =>
            i.Method.Name == nameof(IGroupsClient.GetGroupTransactionsAsync) && (Guid)i.Arguments[0] == groupId);

    // ---- Two selections, answered in the other order -----------------------------------------

    [Fact]
    public async Task The_answer_about_the_group_that_was_left_does_not_land_on_the_one_that_is_open()
    {
        var page = Build();
        await page.IsReadyTask;
        Assert.Equal(Flat.Id, page.SelectedGroup?.Id);

        _tripIsSlow = true;

        // The trip is opened and its read goes out and does not come back yet.
        page.SelectedGroup = Trip;
        var tripLoad = page.EnsureSelectedLoadedAsync();

        // Then the flat is opened, and answers straight away. This is what is on screen.
        page.SelectedGroup = Flat;
        await page.EnsureSelectedLoadedAsync();

        Assert.Equal("Dinner in Flat", Assert.Single(page.Transactions!.Items).Name);
        Assert.Equal(40m, page.Balance!.NetBalances.Single().Balance);

        // Now the trip answers. Nobody is looking at the trip.
        _tripAnswers.SetResult();
        await tripLoad;

        Assert.Equal(Flat.Id, page.SelectedGroup?.Id);
        Assert.Equal("Dinner in Flat", Assert.Single(page.Transactions!.Items).Name);
        Assert.Equal(40m, page.Balance!.NetBalances.Single().Balance);
        Assert.False(page.IsLoading);
    }

    // ---- What is on screen in the meantime ---------------------------------------------------

    /// <summary>
    /// The figures of the group being left go the moment another is selected. Under the
    /// new group's name they were the wrong answer for as long as the server took, and a
    /// page with nothing on it is at least not a page with somebody else's balances on it.
    /// </summary>
    [Fact]
    public async Task Selecting_another_group_clears_the_previous_figures_at_once()
    {
        var page = Build();
        await page.IsReadyTask;
        Assert.NotNull(page.Transactions);

        _tripIsSlow = true;

        page.SelectedGroup = Trip;

        Assert.Null(page.Transactions);
        Assert.Null(page.Balance);
        Assert.True(page.IsLoading);

        _tripAnswers.SetResult();
        await page.EnsureSelectedLoadedAsync();

        Assert.Equal("Dinner in Weekend in Lisbon", Assert.Single(page.Transactions!.Items).Name);
        Assert.False(page.IsLoading);
    }

    /// <summary>
    /// The same group handed over again -- the list was re-read after a write -- is not a
    /// change of subject: the figures stay on screen while they are re-read, and the page
    /// is not dimmed.
    /// </summary>
    [Fact]
    public async Task A_re_read_list_keeps_the_figures_on_screen_while_they_refresh()
    {
        var page = Build();
        await page.IsReadyTask;

        var before = page.Transactions;

        await _changes.NotifyGroupsChangedAsync();

        Assert.Equal(Flat.Id, page.SelectedGroup?.Id);
        Assert.NotNull(page.Transactions);
        Assert.NotSame(before, page.Transactions);
        Assert.False(page.IsLoading);
    }

    // ---- What the prerender can hand over ----------------------------------------------------

    /// <summary>
    /// The state as the prerender can persist it: the group the URL asked for is selected,
    /// and the figures beside it are the ones the group selected before it had. The
    /// selection matching the URL used to be taken as proof, and these figures stayed.
    /// </summary>
    [Fact]
    public async Task Restored_figures_belonging_to_another_group_are_read_again()
    {
        var tracker = new GroupsTracker
        {
            Groups = [Flat, Trip],
            SelectedGroup = Trip,
            Transactions = PageOf(Flat.Id),
            Balance = BalanceOf(Flat.Id),
            FiguresGroupId = Flat.Id
        };

        var page = Build(tracker);

        // Restored, so nothing was read to get here.
        Assert.True(page.IsReadyTask.IsCompleted);
        Assert.Equal(0, TransactionReads(Trip.Id));

        await page.EnsureSelectedLoadedAsync();

        Assert.Equal(1, TransactionReads(Trip.Id));
        Assert.Equal("Dinner in Weekend in Lisbon", Assert.Single(page.Transactions!.Items).Name);
        Assert.Equal(96m, page.Balance!.NetBalances.Single().Balance);
    }

    /// <summary>
    /// The other way the prerender can leave it: the selection persisted before the
    /// figures landed at all.
    /// </summary>
    [Fact]
    public async Task A_restored_selection_with_no_figures_reads_them()
    {
        var tracker = new GroupsTracker { Groups = [Flat, Trip], SelectedGroup = Trip };

        var page = Build(tracker);

        Assert.Null(page.Transactions);

        await page.EnsureSelectedLoadedAsync();

        Assert.Equal("Dinner in Weekend in Lisbon", Assert.Single(page.Transactions!.Items).Name);
    }

    /// <summary>
    /// Figures that are the selected group's own are not read again: that is the whole
    /// point of persisting them across the prerender.
    /// </summary>
    [Fact]
    public async Task Restored_figures_that_are_the_groups_own_are_not_read_again()
    {
        var tracker = new GroupsTracker
        {
            Groups = [Flat, Trip],
            SelectedGroup = Trip,
            Transactions = PageOf(Trip.Id),
            Balance = BalanceOf(Trip.Id),
            FiguresGroupId = Trip.Id
        };

        var page = Build(tracker);

        await page.EnsureSelectedLoadedAsync();

        Assert.Equal(0, TransactionReads(Trip.Id));
        Assert.Equal("Dinner in Weekend in Lisbon", Assert.Single(page.Transactions!.Items).Name);
    }

    /// <summary>
    /// A read that fails leaves the panel empty and the figures unclaimed, so the next
    /// visit to the page asks again rather than trusting a blank.
    /// </summary>
    [Fact]
    public async Task A_failed_read_leaves_nothing_behind_and_is_asked_again_next_time()
    {
        var page = Build();
        await page.IsReadyTask;

        _groupsClient
            .Setup(c => c.GetGroupUserBalanceAsync(Trip.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("down", 503, "", new Dictionary<string, IEnumerable<string>>(), null));

        page.SelectedGroup = Trip;
        await page.EnsureSelectedLoadedAsync();

        Assert.Null(page.Transactions);
        Assert.Null(page.Balance);
        Assert.False(page.IsLoading);

        _groupsClient
            .Setup(c => c.GetGroupUserBalanceAsync(Trip.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BalanceOf(Trip.Id));

        // The page opening again asks again, because nothing it holds is the trip's.
        await page.EnsureSelectedLoadedAsync();

        Assert.Equal(96m, page.Balance!.NetBalances.Single().Balance);
    }
}

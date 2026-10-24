using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Groups;
using GroupSplit.App.Shared.Services.Transactions;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The signed-in home page: where the reader stands across every group, what has happened
/// anywhere, and what the groups spent this month.
/// </summary>
/// <remarks>
/// The position and the groups come from the state service. The feed and the month's
/// figures the dashboard reads itself, and reads again whenever a write announces a change
/// to expenses or groups. That second read runs inside the announcement: a write awaits
/// its notify, so a read that threw out of it would fail the write that raised it, after
/// the server had already taken it.
/// <para>
/// The strip under the position has its own test file and is stubbed here.
/// </para>
/// </remarks>
public class DashboardTest : ComponentTest
{
    private static readonly GroupResponse Lisbon = new(Guid.NewGuid(), "Weekend in Lisbon", 3);
    private static readonly GroupResponse Flat = new(Guid.NewGuid(), "The flat", 2);

    private static readonly UserInfo Ana = new(Guid.NewGuid(), "Ana", "Ruiz", "ana@example.com");

    private readonly Mock<IGroupsPageStateService> _groups = new();
    private readonly Mock<ITransactionsPageStateService> _transactions = new();
    private readonly Mock<IUsersClient> _users = new();
    private readonly Mock<ITransactionsClient> _client = new();

    private readonly LocalClock _clock;

    /// <summary>The cross-group feed, as the server holds it.</summary>
    private List<UserActivityResponse> _feed = [Expense("Groceries")];

    private List<GroupResponse> _groupList = [Lisbon, Flat];

    private UserPositionResponse? _position = new(0m, 0m, 0m, []);

    public DashboardTest()
    {
        _clock = ReaderAt(TimeSpan.Zero);

        _groups.SetupGet(state => state.Groups).Returns(() => _groupList);
        _groups.SetupGet(state => state.Position).Returns(() => _position);
        _groups.SetupGet(state => state.IsReadyTask).Returns(Task.CompletedTask);
        _transactions.SetupGet(state => state.IsReadyTask).Returns(Task.CompletedTask);

        _users
            .Setup(client => client.GetCurrentUserActivityAsync(It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new PagedResponseOfUserActivityResponse(_feed, 1, 6, _feed.Count));

        // This month and last, told apart by where each starts, so a card reading the wrong
        // one reads a visibly wrong number.
        var thisMonth = new DateFilter(DateFilterPreset.ThisMonth).Bounds(_clock.Offset, _clock.Today).From;

        _client
            .Setup(client => client.GetTransactionSharesSummaryAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset? from, DateTimeOffset? _, Guid? _, Guid? _, string _, bool? _,
                string _, bool? _, CancellationToken _) =>
                from == thisMonth
                    ? new ExpenseShareSummaryResponse(9, 120m, 40m, 31m)
                    : new ExpenseShareSummaryResponse(14, 80m, 30m, 20m));

        Services.AddSingleton(_groups.Object);
        Services.AddSingleton(_transactions.Object);
        Services.AddSingleton(_users.Object);
        Services.AddSingleton(_client.Object);

        ComponentFactories.AddStub<WaitingOnYou>();
    }

    private static UserActivityResponse Expense(string name) => new()
    {
        Id = Guid.NewGuid(),
        Kind = ActivityKind.Expense,
        Name = name,
        Amount = 12m,
        DateTime = DateTimeOffset.UtcNow,
        PaidByUserName = "Omar Rivero",
        GroupId = Lisbon.Id,
        GroupName = Lisbon.Name
    };

    private IRenderedComponent<Dashboard> Open() =>
        Render<Dashboard>(parameters => parameters.Add(page => page.UserInfo, Ana));

    [Fact]
    public void Pending_groups_do_not_render_zero_balances_or_empty_content()
    {
        var ready = new TaskCompletionSource();
        _groups.SetupGet(state => state.IsReadyTask).Returns(ready.Task);

        var page = Open();

        Assert.Single(page.FindAll("[role='status'][aria-busy='true']"));
        Assert.DoesNotContain("All settled", page.Markup);
        Assert.DoesNotContain("No groups yet", page.Markup);
        Assert.DoesNotContain("Nothing recorded yet", page.Markup);

        ready.SetResult();
        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup));
    }

    private static string Text(AngleSharp.Dom.IElement element) =>
        System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

    private void FeedFails() =>
        _users
            .Setup(client => client.GetCurrentUserActivityAsync(It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

    private void MonthFails() =>
        _client
            .Setup(client => client.GetTransactionSharesSummaryAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
                It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(500, "INTERNAL"));

    private void ShownOnce(string context) =>
        Snackbar.Verify(snackbar => snackbar.Add(
            It.Is<string>(message => message.StartsWith(context, StringComparison.Ordinal)),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);

    // ---- what it draws --------------------------------------------------------------------

    /// <summary>
    /// The page leads with the position, and what is owed to the reader and what they owe
    /// are kept apart: they are different people, so they do not cancel out.
    /// </summary>
    [Fact]
    public void The_position_leads_and_names_each_group_that_is_not_settled()
    {
        var settled = new GroupResponse(Guid.NewGuid(), "Book club", 5);
        _groupList = [Lisbon, Flat, settled];
        _position = new UserPositionResponse(15m, 40m, 25m,
        [
            new GroupPosition(Lisbon.Id, Lisbon.Name, 40m, false),
            new GroupPosition(Flat.Id, Flat.Name, -25m, false),
            new GroupPosition(settled.Id, settled.Name, 0m, false)
        ]);

        var page = Open();

        var hero = page.Find(".gs-hero");
        Assert.Equal("Overall, you are owed", Text(hero.QuerySelector(".gs-eyebrow")!));
        Assert.Equal("$15.00", Text(hero.QuerySelector(".gs-hero-amount")!));
        Assert.StartsWith("$40.00 owed to you, $25.00 owed by you.", Text(hero.QuerySelector(".gs-hero-sub")!),
            StringComparison.Ordinal);

        var rows = hero.QuerySelectorAll(".gs-hero-row .who").Select(Text).ToList();
        Assert.Equal(["Weekend in Lisbon owes you", "The flat you owe"], rows);
    }

    [Fact]
    public void Owing_overall_says_so_with_the_amount_unsigned()
    {
        _position = new UserPositionResponse(-25m, 0m, 25m, [new GroupPosition(Flat.Id, Flat.Name, -25m, false)]);

        var hero = Open().Find(".gs-hero");

        Assert.Equal("Overall, you owe", Text(hero.QuerySelector(".gs-eyebrow")!));
        Assert.Equal("$25.00", Text(hero.QuerySelector(".gs-hero-amount")!));
        Assert.Equal("Across 1 group.", Text(hero.QuerySelector(".gs-hero-sub")!));
    }

    [Fact]
    public void Nothing_outstanding_reads_all_settled()
    {
        _position = new UserPositionResponse(0m, 0m, 0m, [new GroupPosition(Lisbon.Id, Lisbon.Name, 0m, false)]);

        var hero = Open().Find(".gs-hero");

        Assert.Equal("All settled", Text(hero.QuerySelector(".gs-hero-amount")!));
        Assert.Equal("Nothing outstanding in any of your groups.", Text(hero.QuerySelector(".gs-hero-sub")!));
        Assert.Empty(hero.QuerySelectorAll(".gs-hero-row"));
    }

    /// <summary>
    /// The feed is everybody's expenses in every group, six of them, and the month's figure
    /// is what the groups spent around the reader -- shared only -- beside last month's.
    /// </summary>
    [Fact]
    public void The_feed_and_the_month_are_read_and_drawn()
    {
        _feed = [Expense("Groceries"), Expense("Dinner at Ramiro")];

        var page = Open();

        page.WaitForAssertion(() =>
            Assert.Contains("Dinner at Ramiro", page.Markup, StringComparison.Ordinal));
        Assert.Contains("Groceries", page.Markup, StringComparison.Ordinal);

        _users.Verify(client => client.GetCurrentUserActivityAsync(1, 6, It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(client => client.GetTransactionSharesSummaryAsync(
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), null, null, null, false, null, null,
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        var month = page.Find(".gs-stat");
        Assert.Equal("$120.00", Text(month.QuerySelector(".gs-stat-value")!));
        Assert.Equal("9 expenses · $80.00 last month", Text(month.QuerySelector(".gs-muted")!));
    }

    /// <summary>
    /// Archiving tidies the list of groups and does not forgive a debt: the archived group
    /// leaves "Your groups" and stays in the position.
    /// </summary>
    [Fact]
    public void An_archived_group_leaves_the_list_and_its_balance_stays_in_the_position()
    {
        var archived = new GroupResponse(Guid.NewGuid(), "Ski trip 2024", 4, IsArchive: true);
        _groupList = [Lisbon, archived];
        _position = new UserPositionResponse(-30m, 0m, 30m, [new GroupPosition(archived.Id, archived.Name, -30m, true)]);

        var page = Open();

        Assert.Contains(page.FindAll(".gs-hero-row .who"), who => Text(who) == "Ski trip 2024 you owe");
        Assert.DoesNotContain(page.FindAll(".gs-list .gs-row .title"), title => Text(title) == "Ski trip 2024");
        Assert.Contains(page.FindAll(".gs-list .gs-row .title"), title => Text(title) == "Weekend in Lisbon");
    }

    /// <summary>
    /// Each group row says where the reader stands in it, except a personal group, which
    /// has nobody in it to settle with.
    /// </summary>
    [Fact]
    public void Your_groups_says_where_you_stand_in_each_and_opens_it()
    {
        var mine = new GroupResponse(Ana.Id, "Personal", 1) { IsPersonal = true };
        _groupList = [Lisbon, Flat, mine];
        _position = new UserPositionResponse(40m, 40m, 0m, [new GroupPosition(Lisbon.Id, Lisbon.Name, 40m, false)]);

        var page = Open();

        var rows = page.FindAll("a.gs-row[role=button]");
        AngleSharp.Dom.IElement Row(string name) =>
            rows.Single(row => row.QuerySelector(".title")!.TextContent.Trim() == name);

        Assert.Equal("3 members", Text(Row("Weekend in Lisbon").QuerySelector(".meta")!));
        Assert.Equal("+$40.00", Text(Row("Weekend in Lisbon").QuerySelector(".gs-money")!));
        Assert.Equal("owed to you", Text(Row("Weekend in Lisbon").QuerySelector(".when")!));

        Assert.Equal("$0.00", Text(Row("The flat").QuerySelector(".gs-money")!));
        Assert.Equal("settled", Text(Row("The flat").QuerySelector(".when")!));

        Assert.Equal("Just you", Text(Row("Personal").QuerySelector(".meta")!));
        Assert.Null(Row("Personal").QuerySelector(".gs-money"));

        Row("The flat").Click();

        Assert.EndsWith($"/groups/{Flat.Id}", Services.GetRequiredService<NavigationManager>().Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_groups_yet_says_how_to_start()
    {
        _groupList = [];

        var page = Open();

        Assert.Contains("No groups yet", page.Markup, StringComparison.Ordinal);
    }

    // ---- re-reading on a change -----------------------------------------------------------

    /// <summary>
    /// A write anywhere -- an expense, a settlement, somebody joining a group -- is in the
    /// feed and the month's figure as soon as it is announced.
    /// </summary>
    [Theory]
    [InlineData("transactions")]
    [InlineData("groups")]
    public async Task An_announced_change_re_reads_the_feed_and_the_month(string change)
    {
        var page = Open();
        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup, StringComparison.Ordinal));

        _feed = [Expense("Taxi to the airport"), Expense("Groceries")];

        await page.InvokeAsync(() => change == "transactions"
            ? Changes.NotifyTransactionsChangedAsync()
            : Changes.NotifyGroupsChangedAsync());

        page.WaitForAssertion(() =>
            Assert.Contains("Taxi to the airport", page.Markup, StringComparison.Ordinal));

        _users.Verify(client => client.GetCurrentUserActivityAsync(1, 6, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _client.Verify(client => client.GetTransactionSharesSummaryAsync(
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(),
            It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<bool?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    /// <summary>
    /// A write raises two announcements and the page reads on both, and nothing promises
    /// the first read answers first. The newer read's answer is the one shown.
    /// </summary>
    [Fact]
    public async Task An_older_read_that_lands_last_does_not_overwrite_a_newer_one()
    {
        var page = Open();
        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup, StringComparison.Ordinal));

        var slow = new TaskCompletionSource<PagedResponseOfUserActivityResponse>();

        _users
            .SetupSequence(client => client.GetCurrentUserActivityAsync(It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(slow.Task)
            .ReturnsAsync(new PagedResponseOfUserActivityResponse([Expense("Newer")], 1, 6, 1));

        // Held: this read is waiting on the slow answer.
        var first = page.InvokeAsync(() => Changes.NotifyTransactionsChangedAsync());
        await page.InvokeAsync(() => Changes.NotifyGroupsChangedAsync());

        page.WaitForAssertion(() => Assert.Contains("Newer", page.Markup, StringComparison.Ordinal));

        slow.SetResult(new PagedResponseOfUserActivityResponse([Expense("Older")], 1, 6, 1));
        await first;

        Assert.Contains("Newer", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Older", page.Markup, StringComparison.Ordinal);
    }

    // ---- when a read fails ----------------------------------------------------------------

    /// <summary>
    /// A feed that cannot be read is said once, and the rest of the page still draws: the
    /// month's figure is a separate read and does not go down with it.
    /// </summary>
    [Fact]
    public void A_feed_that_cannot_be_read_says_so_and_the_rest_of_the_page_still_draws()
    {
        FeedFails();

        var page = Open();

        page.WaitForAssertion(() => ShownOnce("Could not load your recent activity."));
        Assert.Contains("Could not load this view", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing recorded yet", page.Markup, StringComparison.Ordinal);
        Assert.Equal("$120.00", Text(page.Find(".gs-stat .gs-stat-value")));
    }

    /// <summary>
    /// The read that follows a write runs inside the write's own notify. A failure there is
    /// shown and kept to itself, and what was on the page stays on it: a throw would fail
    /// the expense or the settlement that raised the announcement, after the server had
    /// already recorded it.
    /// </summary>
    [Theory]
    [InlineData("transactions")]
    [InlineData("groups")]
    public async Task A_re_read_that_fails_does_not_throw_out_of_the_announcement(string change)
    {
        var page = Open();
        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup, StringComparison.Ordinal));

        FeedFails();
        MonthFails();

        var thrown = await Record.ExceptionAsync(() => page.InvokeAsync(() => change == "transactions"
            ? Changes.NotifyTransactionsChangedAsync()
            : Changes.NotifyGroupsChangedAsync()));

        Assert.Null(thrown);

        ShownOnce("Could not load your recent activity.");
        ShownOnce("Could not load this month's total.");

        // What the last good read drew, rather than an empty page.
        Assert.Contains("Groceries", page.Markup, StringComparison.Ordinal);
        Assert.Equal("$120.00", Text(page.Find(".gs-stat .gs-stat-value")));
    }

    /// <summary>
    /// The notifier outlives the page, so a page that has gone has to stop listening: one
    /// left subscribed would read for nobody on every write for the rest of the session.
    /// </summary>
    [Fact]
    public async Task Once_disposed_it_no_longer_reads_on_a_change()
    {
        var page = Open();
        page.WaitForAssertion(() => Assert.Contains("Groceries", page.Markup, StringComparison.Ordinal));

        await DisposeComponentsAsync();

        _users.Invocations.Clear();
        _client.Invocations.Clear();

        await Changes.NotifyTransactionsChangedAsync();
        await Changes.NotifyGroupsChangedAsync();

        _users.Verify(client => client.GetCurrentUserActivityAsync(It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyNoOtherCalls();
    }
}

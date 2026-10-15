using AngleSharp.Dom;
using Bunit;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services.Settling;
using GroupSplit.App.Web.Test.Errors;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;

namespace GroupSplit.App.Web.Test.Components;

/// <summary>
/// The Settle page: who to pay and who owes you across every group, the one action that
/// records a payment with a person, and the history that answers "did I already pay this?"
/// (#326).
/// </summary>
/// <remarks>
/// The real state service over a mocked users client. The page is thin and the service is
/// where a recorded payment turns into a re-read plan and history, so mocking the service
/// would pin only that the page calls a method.
/// </remarks>
public class SettlePageTest : ComponentTest
{
    private static readonly Guid Omar = Guid.NewGuid();
    private static readonly Guid Dani = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Trip = Guid.NewGuid();

    private static readonly DateTimeOffset ThirdOfSeptember = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly SettlementPlanResponse Square = new(0m, 0m, 0m, [], [], null);

    private readonly Mock<IUsersClient> _users = new();

    /// <summary>What the server says the plan is, at the moment it is asked.</summary>
    private SettlementPlanResponse _plan = Square;

    /// <summary>Every settlement the server holds, newest first. Asked for a page at a time.</summary>
    private List<SettlementResponse> _history = [];

    public SettlePageTest()
    {
        ReaderAt(TimeSpan.Zero);

        _users
            .Setup(client => client.GetSettlementPlanAsync(It.IsAny<DateOnly?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _plan);

        _users
            .Setup(client => client.GetSettlementsAsync(It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int? _, int? pageSize, CancellationToken _) =>
                new PagedResponseOfSettlementResponse([.. _history.Take(pageSize ?? 5)], 1, pageSize ?? 5,
                    _history.Count));

        Services.AddSingleton(_users.Object);
        Services.AddSingleton<SettleTracker>();
        Services.AddSingleton<ISettleStateService, SettleStateService>();
    }

    /// <summary>Omar, owed 50 from Home and 40 from Trip: one payment for two groups.</summary>
    private static PersonSettlement OmarAcrossBoth { get; } =
        new(Omar, "Omar", 90m, [new GroupDebt(Home, "Home", 50m), new GroupDebt(Trip, "Trip", 40m)]);

    private static PersonSettlement DaniInTrip { get; } = new(Dani, "Dani", 25m, [new GroupDebt(Trip, "Trip", 25m)]);

    private static SettlementResponse Paid(string from, string to, bool byYou, string group,
        string? note = null, DateOnly? through = null) => new()
    {
        Id = Guid.NewGuid(),
        GroupName = group,
        FromUserName = from,
        ToUserName = to,
        Amount = 10m,
        DateTime = ThirdOfSeptember,
        Description = note,
        PaidByYou = byYou,
        CoversThrough = through
    };

    /// <summary>The page, and the provider its dialog renders into.</summary>
    private async Task<(IRenderedComponent<Settle> Page, IRenderedComponent<MudDialogProvider> Dialogs)> Open()
    {
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();

        var page = Render<Settle>();

        await page.InvokeAsync(() => Task.CompletedTask);

        return (page, dialogs);
    }

    private static IElement Row(IRenderedComponent<Settle> page, string name) =>
        page.FindAll(".gs-settle-row")
            .Single(row => row.QuerySelector(".who")!.TextContent.Trim() == name);

    private static IElement DialogButton(IRenderedComponent<MudDialogProvider> dialogs, string text) =>
        dialogs.FindAll("button").Last(button => button.TextContent.Trim() == text);

    private static IReadOnlyList<(string Title, string Meta)> HistoryRows(IRenderedComponent<Settle> page) =>
    [
        .. page.FindAll(".gs-list .gs-row").Select(row => (
            row.QuerySelector(".title")!.TextContent.Trim(),
            row.QuerySelector(".meta")!.TextContent.Trim()))
    ];

    private void VerifyError(string context) =>
        Snackbar.Verify(bar => bar.Add(It.Is<string>(message => message.StartsWith(context, StringComparison.Ordinal)),
            Severity.Error, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);

    /// <summary>
    /// The case the page exists for. Two debts with one person in two groups are one
    /// payment, the confirmation names both groups, and the page re-reads the plan and the
    /// history so the person is gone from one and in the other.
    /// </summary>
    [Fact]
    public async Task Recording_a_payment_settles_it_in_every_group_it_came_from()
    {
        _plan = new SettlementPlanResponse(-90m, 0m, 90m, [OmarAcrossBoth], [], null);

        _users
            .Setup(client => client.SettleWithPersonAsync(It.IsAny<SettleWithPersonRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SettleWithPersonRequest request, CancellationToken _) =>
            {
                _plan = Square with { LastSettled = ThirdOfSeptember };
                _history = [Paid("You", "Omar", byYou: true, "Home")];

                return new SettleWithPersonResponse(Omar, "Omar", request.Amount, request.Direction,
                    ThirdOfSeptember, null, OmarAcrossBoth.Groups);
            });

        var (page, dialogs) = await Open();

        var omar = Row(page, "Omar");
        Assert.Equal("$50.00 in Home · $40.00 in Trip", omar.QuerySelector(".meta")!.TextContent.Trim());

        // Held rather than awaited: the click is waiting on the dialog it just opened.
        var recording = omar.QuerySelector("button")!.ClickAsync(new());

        dialogs.WaitForAssertion(() => Assert.Contains("You owe Omar $90.00", dialogs.Markup, StringComparison.Ordinal));

        await DialogButton(dialogs, "I paid them").ClickAsync(new());
        await recording;

        _users.Verify(client => client.SettleWithPersonAsync(
            It.Is<SettleWithPersonRequest>(request =>
                request.UserId == Omar && request.Amount == 90m &&
                request.Direction == SettlementDirection.YouPaidThem && request.Through == null),
            It.IsAny<CancellationToken>()), Times.Once);

        Snackbar.Verify(bar => bar.Add("Recorded $90.00 paid to Omar across Home and Trip.", Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll(".gs-settle-row"));
            Assert.Equal("All settled", page.Find(".gs-empty h3").TextContent.Trim());
            Assert.Equal(("You paid Omar", "Home"), Assert.Single(HistoryRows(page)));
        });
    }

    /// <summary>
    /// A row under "You are owed" records the other way round: the request says they paid,
    /// and so does the confirmation.
    /// </summary>
    [Fact]
    public async Task A_payment_from_somebody_who_owes_you_is_recorded_as_theirs()
    {
        _plan = new SettlementPlanResponse(25m, 25m, 0m, [], [DaniInTrip], null);

        _users
            .Setup(client => client.SettleWithPersonAsync(It.IsAny<SettleWithPersonRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SettleWithPersonRequest request, CancellationToken _) =>
                new SettleWithPersonResponse(Dani, "Dani", request.Amount, request.Direction,
                    ThirdOfSeptember, null, DaniInTrip.Groups));

        var (page, dialogs) = await Open();

        var recording = Row(page, "Dani").QuerySelector("button")!.ClickAsync(new());

        dialogs.WaitForAssertion(() => Assert.Contains("Dani owes you $25.00", dialogs.Markup, StringComparison.Ordinal));

        await DialogButton(dialogs, "They paid me").ClickAsync(new());
        await recording;

        _users.Verify(client => client.SettleWithPersonAsync(
            It.Is<SettleWithPersonRequest>(request =>
                request.UserId == Dani && request.Amount == 25m &&
                request.Direction == SettlementDirection.TheyPaidYou),
            It.IsAny<CancellationToken>()), Times.Once);

        Snackbar.Verify(bar => bar.Add("Recorded $25.00 from Dani in Trip.", Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Closing_the_dialog_records_nothing()
    {
        _plan = new SettlementPlanResponse(-90m, 0m, 90m, [OmarAcrossBoth], [], null);

        var (page, dialogs) = await Open();

        var recording = Row(page, "Omar").QuerySelector("button")!.ClickAsync(new());
        dialogs.WaitForAssertion(() => Assert.Contains("You owe Omar $90.00", dialogs.Markup, StringComparison.Ordinal));

        await DialogButton(dialogs, "Cancel").ClickAsync(new());
        await recording;

        _users.Verify(client => client.SettleWithPersonAsync(It.IsAny<SettleWithPersonRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The server can still say no -- the debt was settled from another device in the
    /// meantime, most likely. The page says what did not happen and leaves the person on
    /// it: nothing re-read, nothing claimed as recorded.
    /// </summary>
    [Fact]
    public async Task A_refused_payment_says_so_and_leaves_the_person_to_pay()
    {
        _plan = new SettlementPlanResponse(-90m, 0m, 90m, [OmarAcrossBoth], [], null);

        _users
            .Setup(client => client.SettleWithPersonAsync(It.IsAny<SettleWithPersonRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(Refusals.Of(409, ErrorCodes.SettlementNothingToSettle));

        var (page, dialogs) = await Open();

        var recording = Row(page, "Omar").QuerySelector("button")!.ClickAsync(new());
        dialogs.WaitForAssertion(() => Assert.Contains("You owe Omar $90.00", dialogs.Markup, StringComparison.Ordinal));

        await DialogButton(dialogs, "I paid them").ClickAsync(new());
        await recording;

        VerifyError("Could not record the payment with Omar.");
        Snackbar.Verify(bar => bar.Add(It.IsAny<string>(), Severity.Success,
            It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Never);

        // Read once, when the page opened, and not again: a refusal announces no change.
        _users.Verify(client => client.GetSettlementPlanAsync(It.IsAny<DateOnly?>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("I paid them", Row(page, "Omar").QuerySelector("button")!.TextContent.Trim());
    }

    /// <summary>
    /// A plan that never arrived is not a plan with nothing in it: the error says the plan
    /// could not be loaded, and nothing on the page says the reader is square (#435). The
    /// hero used to, because a missing plan made the net figure zero.
    /// </summary>
    [Fact]
    public async Task A_plan_that_could_not_be_loaded_is_not_shown_as_nothing_owed()
    {
        PlanUnreachable();

        var (page, _) = await Open();

        VerifyError("Could not load your settlement plan.");

        Assert.Empty(page.FindAll(".gs-settle-row"));
        Assert.DoesNotContain("All settled", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing owed either way, in any group", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Not loaded", Hero(page), StringComparison.Ordinal);
        Assert.Contains("Could not load what you owe and are owed.", Hero(page), StringComparison.Ordinal);
        Assert.Contains("Could not work out what clears everything.", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trying_the_plan_again_shows_it_once_it_loads()
    {
        PlanUnreachable();
        var (page, _) = await Open();

        _plan = new SettlementPlanResponse(-90m, 0m, 90m, [OmarAcrossBoth], [], null);
        _users
            .Setup(client => client.GetSettlementPlanAsync(It.IsAny<DateOnly?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _plan);

        await TryAgainAsync(page, ".gs-hero");

        page.WaitForAssertion(() => Assert.Contains("Overall, you owe", Hero(page), StringComparison.Ordinal));
        Assert.Single(page.FindAll(".gs-settle-row"));
        Assert.DoesNotContain("Not loaded", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>Before the plan has answered, the hero says nothing about where anybody stands.</summary>
    [Fact]
    public void A_plan_still_being_read_is_not_shown_as_all_settled()
    {
        var answer = new TaskCompletionSource<SettlementPlanResponse>();

        _users
            .Setup(client => client.GetSettlementPlanAsync(It.IsAny<DateOnly?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(answer.Task);

        var page = Render<Settle>();

        Assert.DoesNotContain("All settled", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Not loaded", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Working out what clears everything.", page.Markup, StringComparison.Ordinal);

        answer.SetResult(new SettlementPlanResponse(-90m, 0m, 90m, [OmarAcrossBoth], [], null));

        page.WaitForAssertion(() => Assert.Contains("Overall, you owe", Hero(page), StringComparison.Ordinal));
    }

    /// <summary>
    /// A history that did not load says so, rather than "Loading…" for good with nothing in
    /// flight (#435).
    /// </summary>
    [Fact]
    public async Task A_history_that_could_not_be_loaded_says_so_rather_than_loading_for_good()
    {
        _plan = new SettlementPlanResponse(-90m, 0m, 90m, [OmarAcrossBoth], [], null);
        HistoryUnreachable();

        var (page, _) = await Open();

        VerifyError("Could not load your settlements.");

        Assert.DoesNotContain("Loading…", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Could not load your settlements.", page.Markup, StringComparison.Ordinal);

        // A separate read, drawn as usual.
        Assert.Contains("Overall, you owe", Hero(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trying_the_history_again_shows_it_once_it_loads()
    {
        HistoryUnreachable();
        var (page, _) = await Open();

        _history = [Paid("You", "Omar", byYou: true, "Home")];
        _users
            .Setup(client => client.GetSettlementsAsync(It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int? _, int? pageSize, CancellationToken _) =>
                new PagedResponseOfSettlementResponse([.. _history], 1, pageSize ?? 5, _history.Count));

        await TryAgainAsync(page, ".gs-card-pad .gs-empty");

        page.WaitForAssertion(() => Assert.Equal([("You paid Omar", "Home")], HistoryRows(page)));
        Assert.DoesNotContain("Could not load your settlements.", page.Markup, StringComparison.Ordinal);
    }

    private void PlanUnreachable() =>
        _users
            .Setup(client => client.GetSettlementPlanAsync(It.IsAny<DateOnly?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the server could not be reached"));

    private void HistoryUnreachable() =>
        _users
            .Setup(client => client.GetSettlementsAsync(It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the server could not be reached"));

    private static string Hero(IRenderedComponent<Settle> page) => page.Find(".gs-hero").TextContent;

    private static Task TryAgainAsync(IRenderedComponent<Settle> page, string within) =>
        page.Find(within).QuerySelectorAll("button")
            .Single(button => button.TextContent.Trim() == "Try again")
            .ClickAsync(new());

    /// <summary>
    /// Nothing owed either way is said once, in the empty state, with when it last was not
    /// -- and neither list is drawn to be empty under it.
    /// </summary>
    [Fact]
    public async Task Nothing_owed_either_way_is_all_settled_since_the_last_payment()
    {
        _plan = Square with { LastSettled = ThirdOfSeptember };

        var (page, _) = await Open();

        var empty = page.Find(".gs-card.gs-empty");
        Assert.Equal("All settled", empty.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("Nothing owed either way, in any group since 3 September 2026.",
            System.Text.RegularExpressions.Regex.Replace(empty.TextContent, @"\s+", " "), StringComparison.Ordinal);

        Assert.DoesNotContain("You pay", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("You are owed", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll(".gs-settle-row"));
    }

    /// <summary>
    /// Which group a repayment landed in is what the history is for, since the plan above
    /// hides it. Each row reads from the reader's end, with the note and the period after
    /// the group.
    /// </summary>
    [Fact]
    public async Task The_history_says_who_paid_whom_and_in_which_group()
    {
        _history =
        [
            Paid("You", "Omar", byYou: true, "Home", note: "cash"),
            Paid("Dani", "You", byYou: false, "Trip", through: new DateOnly(2026, 8, 31))
        ];

        var (page, _) = await Open();

        Assert.Equal(
            [("You paid Omar", "Home · cash"), ("Dani paid you", "Trip · through 31 Aug")],
            HistoryRows(page));
        Assert.DoesNotContain("Show more", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The history grows rather than pages, so what was already read stays above what
    /// "Show more" adds.
    /// </summary>
    [Fact]
    public async Task Show_more_reads_a_longer_history_and_goes_once_it_is_all_there()
    {
        _history = [.. Enumerable.Range(1, 7).Select(n => Paid("You", $"Person {n}", byYou: true, "Home"))];

        var (page, _) = await Open();

        Assert.Equal(5, HistoryRows(page).Count);
        Assert.Equal("7", page.Find(".gs-card-title .gs-tag").TextContent.Trim());

        await page.FindAll("button").Single(button => button.TextContent.Trim() == "Show more").ClickAsync(new());

        _users.Verify(client => client.GetSettlementsAsync(1, 10, It.IsAny<CancellationToken>()), Times.Once);

        page.WaitForAssertion(() =>
        {
            Assert.Equal(
                [.. Enumerable.Range(1, 7).Select(n => $"You paid Person {n}")],
                HistoryRows(page).Select(row => row.Title));
            Assert.DoesNotContain("Show more", page.Markup, StringComparison.Ordinal);
        });
    }
}

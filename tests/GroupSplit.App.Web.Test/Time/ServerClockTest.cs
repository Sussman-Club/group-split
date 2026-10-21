using Bunit;
using GroupSplit.App.Shared.Components;
using GroupSplit.App.Shared.Layout;
using GroupSplit.App.Shared.Models;
using GroupSplit.App.Shared.Pages;
using GroupSplit.App.Shared.Services;
using GroupSplit.App.Shared.Services.Settling;
using GroupSplit.App.Web.Test.Components;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Time;

/// <summary>
/// Under the server host the clock learns the browser's offset only after the first render,
/// from the layout, by which time the page under it has already read and drawn with the
/// server's own offset (#434).
/// </summary>
/// <remarks>
/// <see cref="LocalClockTest"/> pins the clock itself. This pins what the layout does with
/// the answer: the page under it has to show the person's days, and read again, without
/// anybody navigating. Redrawing the layout alone did not reach a page whose parameters had
/// not changed.
/// <para>
/// The fallback is the machine's own offset, so the browser here is put twelve hours away
/// from it, whatever zone the tests run in.
/// </para>
/// </remarks>
public class ServerClockTest : ComponentTest
{
    private static readonly TimeSpan Machine = DateTimeOffset.Now.Offset;

    private static readonly TimeSpan Browser = Machine >= TimeSpan.Zero
        ? Machine - TimeSpan.FromHours(12)
        : Machine + TimeSpan.FromHours(12);

    /// <summary>
    /// Half past midnight on 1 September for whichever of the two is ahead, so it is still
    /// 31 August for the other.
    /// </summary>
    private static readonly DateTimeOffset Paid =
        new(2026, 9, 1, 0, 30, 0, Browser > Machine ? Browser : Machine);

    private readonly Mock<ISettleStateService> _settling = new();

    public ServerClockTest()
    {
        // The layout's own furniture reads state services of its own, none of them about dates.
        ComponentFactories.AddStub<AppThemeProvider>();
        ComponentFactories.AddStub<NavMenu>();
        ComponentFactories.AddStub<AppBar>();
        ComponentFactories.AddStub<AndroidUpdateNotice>();

        _settling.Setup(state => state.Plan).Returns(new SettlementPlanResponse(0m, 0m, 0m, [], [], null));
        _settling.Setup(state => state.History).Returns(new PagedResponse<SettlementResponse>(
        [
            new SettlementResponse
            {
                GroupName = "The flat", ToUserName = "Omar", PaidByYou = true, Amount = 40m, DateTime = Paid
            }
        ], 1, 5, 1));

        Services.AddSingleton(_settling.Object);
    }

    /// <summary>A server-side clock, whose browser answers <paramref name="offset"/> once asked.</summary>
    private void BrowserAt(TimeSpan offset)
    {
        var js = new Mock<IJSRuntime>();

        js.Setup(runtime => runtime.InvokeAsync<int>("gs.tzOffsetMinutes", It.IsAny<object?[]?>()))
            .Returns(ValueTask.FromResult((int)offset.TotalMinutes));

        Services.AddSingleton(new LocalClock(js.Object));
    }

    private IRenderedComponent<LayoutView> RenderSettleUnderTheLayout() =>
        Render<LayoutView>(parameters => parameters
            .Add(view => view.Layout, typeof(MainLayout))
            .Add(view => view.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<Settle>(0);
                builder.CloseComponent();
            })));

    private static string DayIn(TimeSpan offset) => TransactionDay.Of(Paid, offset).ToString("d MMM");

    [Fact]
    public void A_page_drawn_before_the_browser_answered_shows_the_persons_day_once_it_has()
    {
        Assert.NotEqual(DayIn(Machine), DayIn(Browser));
        BrowserAt(Browser);

        var page = RenderSettleUnderTheLayout();

        page.WaitForAssertion(() =>
            Assert.Equal(DayIn(Browser), page.Find(".gs-list .when").TextContent.Trim()));
    }

    /// <summary>
    /// Drawing again is not enough for a page that read a span with the server's offset, so
    /// the page is built again and its first reads run with the person's.
    /// </summary>
    [Fact]
    public void A_page_that_read_before_the_browser_answered_reads_again_once_it_has()
    {
        BrowserAt(Browser);

        var page = RenderSettleUnderTheLayout();

        page.WaitForAssertion(() =>
            _settling.Verify(state => state.EnsureLoadedAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)));
    }

    /// <summary>
    /// The usual case, where the browser agrees with the server, or WebAssembly asked before
    /// anything rendered: nothing is built twice.
    /// </summary>
    [Fact]
    public void A_browser_that_agrees_with_the_server_leaves_the_page_alone()
    {
        BrowserAt(Machine);

        var page = RenderSettleUnderTheLayout();

        Assert.Equal(DayIn(Machine), page.Find(".gs-list .when").TextContent.Trim());
        _settling.Verify(state => state.EnsureLoadedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

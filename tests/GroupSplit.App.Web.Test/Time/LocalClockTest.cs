using GroupSplit.App.Shared.Services;
using Microsoft.JSInterop;
using Moq;

namespace GroupSplit.App.Web.Test.Time;

/// <summary>
/// The clock both hosting modes read, and the reason it exists: an instant has to show as
/// the same day whichever of them is serving.
/// </summary>
/// <remarks>
/// The app runs as interactive server or as WebAssembly, chosen by configuration, and the
/// two reach the browser differently -- WebAssembly can ask synchronously and the server has
/// to wait for its first render. Anything that reads <c>LocalDateTime</c> instead therefore
/// resolves against a different zone in each: the browser's under WebAssembly, the host's
/// under the server. That is a real difference on a real deployment, not a theoretical one,
/// and it is what these pin.
/// </remarks>
public class LocalClockTest
{
    /// <summary>What the browser reports for UTC-5, as <c>gs.tzOffsetMinutes</c> signs it.</summary>
    private const int BrowserOffsetMinutes = -300;

    /// <summary>
    /// Midnight UTC: the instant an expense filed from a bank row is stored at, and the one
    /// that falls on the previous day for anybody west of UTC.
    /// </summary>
    private static readonly DateTimeOffset Instant = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WebAssembly_has_the_persons_offset_before_anything_has_rendered()
    {
        var clock = new LocalClock(InProcess());

        // No InitializeAsync: the constructor asked, because in this host it can. A date
        // drawn on the very first frame is already the person's.
        Assert.Equal(TimeSpan.FromMinutes(BrowserOffsetMinutes), clock.Offset);
        Assert.Equal(new DateTime(2026, 8, 31, 19, 0, 0), clock.Local(Instant).DateTime);
    }

    [Fact]
    public async Task The_server_has_it_once_the_first_render_has_asked()
    {
        var clock = new LocalClock(OutOfProcess());

        await clock.InitializeAsync();

        Assert.Equal(TimeSpan.FromMinutes(BrowserOffsetMinutes), clock.Offset);
        Assert.Equal(new DateTime(2026, 8, 31, 19, 0, 0), clock.Local(Instant).DateTime);
    }

    /// <summary>
    /// The claim the whole thing rests on, stated as one assertion: the same instant reads
    /// as the same day under either host.
    /// </summary>
    [Fact]
    public async Task Both_hosts_show_the_same_day_for_the_same_instant()
    {
        var wasm = new LocalClock(InProcess());

        var server = new LocalClock(OutOfProcess());
        await server.InitializeAsync();

        Assert.Equal(wasm.Offset, server.Offset);

        // As it is actually written on screen -- "last checked", and the date under a
        // duplicate suggestion -- and not merely as an offset. Named as well as compared,
        // so agreeing on the wrong day is not a way to pass this.
        var day = $"{new DateTime(2026, 8, 31):d}";

        Assert.Equal(day, $"{wasm.Local(Instant):d}");
        Assert.Equal(day, $"{server.Local(Instant):d}");
    }

    /// <summary>
    /// Prerendering, where there is no browser to ask yet. The runtime's own offset stands
    /// in, which is self-consistent, and the interactive render that follows corrects it --
    /// so this only has to not throw and not get stuck.
    /// </summary>
    [Fact]
    public async Task No_browser_to_ask_yet_leaves_the_fallback_and_asks_again_later()
    {
        var js = new Mock<IJSRuntime>();

        js.SetupSequence(runtime => runtime.InvokeAsync<int>("gs.tzOffsetMinutes", It.IsAny<object?[]?>()))
            .Throws(new InvalidOperationException("JavaScript interop calls cannot be issued during prerendering."))
            .Returns(ValueTask.FromResult(BrowserOffsetMinutes));

        var clock = new LocalClock(js.Object);

        Assert.False(await clock.InitializeAsync());

        // Not marked resolved by a failure, so the render that does have a browser gets it.
        Assert.True(await clock.InitializeAsync());
        Assert.Equal(TimeSpan.FromMinutes(BrowserOffsetMinutes), clock.Offset);
    }

    /// <summary>WebAssembly's runtime, which answers synchronously.</summary>
    private static IJSInProcessRuntime InProcess()
    {
        var js = new Mock<IJSInProcessRuntime>();

        js.Setup(runtime => runtime.Invoke<int>("gs.tzOffsetMinutes", It.IsAny<object?[]?>()))
            .Returns(BrowserOffsetMinutes);

        return js.Object;
    }

    /// <summary>The server's, which has to be awaited and only works once there is a circuit.</summary>
    private static IJSRuntime OutOfProcess()
    {
        var js = new Mock<IJSRuntime>();

        js.Setup(runtime => runtime.InvokeAsync<int>("gs.tzOffsetMinutes", It.IsAny<object?[]?>()))
            .Returns(ValueTask.FromResult(BrowserOffsetMinutes));

        return js.Object;
    }
}

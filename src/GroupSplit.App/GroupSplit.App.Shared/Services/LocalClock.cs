using Microsoft.JSInterop;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// What time it is where the person is sitting, asked of the browser rather than of .NET.
/// </summary>
/// <remarks>
/// Every instant is stored and sent as UTC; this is the one place that turns UTC into the
/// person's day and time and back. The expense dialog used to do that itself, from two .NET
/// calls -- <c>DateTime.Now</c> for the wall clock and <c>TimeZoneInfo.Local.GetUtcOffset</c>
/// for the offset -- and under WebAssembly the two did not agree: the first came back as UTC,
/// the second as the browser's real offset, and combining them stored an expense recorded at
/// 12:38 in New York as 20:38 UTC, eight hours ahead. A settlement made thirty seconds later
/// then sorted <em>below</em> it in the group's activity, which is how it was noticed.
/// <para>
/// The browser always knows its own offset, so that is the source. On WebAssembly the call
/// is synchronous and made once, up front; on the server the layout asks asynchronously on
/// first render, and until it has answered the runtime's own offset stands in -- which is
/// self-consistent even where its idea of the zone is not. Nothing else in the app reads
/// <c>TimeZoneInfo.Local</c>, <c>DateTime.Now</c> or <c>DateTime.Today</c>: a "today" or a
/// "now" that does not come from here is the bug above waiting to happen again.
/// </para>
/// </remarks>
public sealed class LocalClock
{
    private readonly IJSRuntime _js;
    private TimeSpan _offset = DateTimeOffset.Now.Offset;
    private bool _resolved;

    public LocalClock(IJSRuntime js)
    {
        _js = js;

        // WebAssembly can ask synchronously, so the first render already has the right
        // answer and nothing shows a UTC date for a frame.
        if (js is IJSInProcessRuntime inProcess)
        {
            try
            {
                Apply(inProcess.Invoke<int>("gs.tzOffsetMinutes"));
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                // The script is not there, or JS is not ready. The fallback stands.
            }
        }
    }

    /// <summary>The person's offset from UTC, as the browser reports it.</summary>
    public TimeSpan Offset => _offset;

    /// <summary>Now, in the person's zone.</summary>
    public DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(_offset);

    /// <summary>The person's calendar day. What "this month" and a date picker open on.</summary>
    public DateTime Today => Now.Date;

    /// <summary>A stored instant in the person's zone, so it shows the day and time they would recognise.</summary>
    public DateTimeOffset Local(DateTimeOffset instant) => instant.ToOffset(_offset);

    /// <summary>
    /// A date and a time as the person picked them, made into the instant they meant --
    /// which is then sent as UTC, like everything else.
    /// </summary>
    public DateTimeOffset At(DateTime date, TimeSpan time) =>
        new DateTimeOffset(DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified), _offset)
            .ToUniversalTime();

    /// <summary>
    /// Asks the browser, for a host that could not ask synchronously. Safe to call more
    /// than once and from any host; answers whether the offset changed, so the caller knows
    /// whether anything on screen needs drawing again.
    /// </summary>
    public async ValueTask<bool> InitializeAsync()
    {
        if (_resolved)
            return false;

        try
        {
            var before = _offset;
            Apply(await _js.InvokeAsync<int>("gs.tzOffsetMinutes"));
            return before != _offset;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return false;
        }
    }

    private void Apply(int minutes)
    {
        _offset = TimeSpan.FromMinutes(minutes);
        _resolved = true;
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException;
}

using GroupSplit.App.Shared.Models;
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
    /// The day a transaction's stored instant belongs to on the person's calendar. Not
    /// <c>Local(instant).Date</c>: a bank row is a date stored as midnight UTC, and that is the
    /// previous evening for anybody west of Greenwich. See <see cref="TransactionDay"/>.
    /// </summary>
    public DateOnly Day(DateTimeOffset stored) => TransactionDay.Of(stored, _offset);

    /// <summary>
    /// The time of day a transaction was recorded at, in the person's zone; none for a bank
    /// row, which is a date and has no time. See <see cref="TransactionDay"/>.
    /// </summary>
    public TimeOnly? TimeOf(DateTimeOffset stored) => TransactionDay.TimeOf(stored, _offset);

    /// <summary>
    /// A transaction's day and time as one piece of text -- "9 Oct 2026, 14:32" -- or its day
    /// alone when it is a date with no time. Every place that writes both goes through here,
    /// so none of them can show a bank row at 20:00 the evening before.
    /// </summary>
    public string Stamp(DateTimeOffset stored, string dayFormat, string timeFormat) =>
        TimeOf(stored) is { } time
            ? $"{Day(stored).ToString(dayFormat)}, {time.ToString(timeFormat)}"
            : Day(stored).ToString(dayFormat);

    /// <summary>The person's calendar day, as a <see cref="DateOnly"/> to compare a <see cref="Day"/> with.</summary>
    public DateOnly TodayDay => DateOnly.FromDateTime(Today);

    /// <summary>
    /// A transaction's day as a short list says it: "Today", "Yesterday", a weekday within the
    /// week, then the date. Both sides of the comparison are the person's days, not the
    /// server's, and the row's is read through <see cref="Day"/> -- so a bank row posted
    /// today is "Today" by the bank's date, wherever the person is.
    /// </summary>
    public string Relative(DateTimeOffset stored)
    {
        var day = Day(stored);
        var today = TodayDay;

        return (today.DayNumber - day.DayNumber) switch
        {
            0 => GroupSplit.App.Shared.Localization.UiText.Get("Today"),
            1 => GroupSplit.App.Shared.Localization.UiText.Get("Yesterday"),
            < 7 => day.ToString("dddd"),
            _ when day.Year == today.Year => day.ToString("d MMM"),
            _ => day.ToString("d MMM yyyy")
        };
    }

    /// <summary>
    /// What a date picker and a time picker open on for a stored transaction: its day and
    /// local time, or, for a date with no time, its day and an empty time.
    /// </summary>
    public (DateTime Date, TimeSpan? Time) Pickers(DateTimeOffset stored) =>
        (Day(stored).ToDateTime(TimeOnly.MinValue), TimeOf(stored)?.ToTimeSpan());

    /// <summary>
    /// The instant an edit stores, from what the two pickers hold now.
    /// </summary>
    /// <remarks>
    /// A moment is made from the pickers through <see cref="At"/>, as it always was. A date
    /// with no time -- a bank row -- opens with an empty time (see <see cref="Pickers"/>), and
    /// stays a date until somebody gives it one: left on its day it is stored exactly as it
    /// was, moved to another day it is that day at midnight UTC, and only a time picked for
    /// it makes it a moment.
    /// </remarks>
    public DateTimeOffset Edited(DateTimeOffset original, DateTime? date, TimeSpan? time)
    {
        var day = date ?? Today;

        if (!TransactionDay.IsDate(original) || time is not null)
            return At(day, time ?? TimeSpan.Zero);

        return DateOnly.FromDateTime(day) == Day(original)
            ? original
            : TransactionDay.Stored(day);
    }

    /// <summary>
    /// The offset to send with a day the API counts activity through. See
    /// <see cref="TransactionDay.CutoffOffsetMinutes"/>.
    /// </summary>
    public int CutoffOffsetMinutes => TransactionDay.CutoffOffsetMinutes(_offset);

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

using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services.Users;

public class UserTracker
{
    [PersistentState]
    public UserInfo? User { get; set; }

    [PersistentState]
    public bool LoginAttempted { get; set; }

    /// <summary>
    /// Why the API would not let this person in at all, when that is the reason there is no
    /// <see cref="User"/>. Persisted with the rest, so the interactive render shows what the
    /// prerender found instead of asking again and flashing the app in between.
    /// </summary>
    [PersistentState]
    public SignInRefusal? Refusal { get; set; }
}

/// <summary>
/// The API refused the signed-in person outright -- on every request, not on one action --
/// with a code that says nothing in the app can fix it. Carries the trace id, because the
/// person's next step is support, and that is what support finds the log line by.
/// </summary>
public sealed record SignInRefusal(string Code, string? TraceId);

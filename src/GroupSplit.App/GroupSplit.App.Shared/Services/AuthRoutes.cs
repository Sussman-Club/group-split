using GroupSplit.App.Shared.Services.Errors;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// The BFF endpoints that start and end a Keycloak session, shared by the web
/// and WebAssembly auth services so the two cannot drift apart.
/// </summary>
public static class AuthRoutes
{
    public const string Logout = "/auth/logout";

    /// <summary>
    /// The branded hand-off page. Named here because <see cref="Handoff"/> writes its
    /// query string and the page reads it, and the two have to agree.
    /// </summary>
    public const string HandoffPath = "/login";

    /// <param name="remember">
    /// Whether "Keep me signed in" was ticked. Passed rather than remembered client-side:
    /// the cookie it decides is written by the server, at the end of a round trip through
    /// Keycloak that this page does not survive.
    /// </param>
    public static string Login(string? returnUrl = null, bool remember = false)
    {
        var login = WithReturnUrl("/auth/login", returnUrl);

        return remember ? Append(login, "remember=true") : login;
    }

    /// <summary>
    /// Where to send somebody whose session has ended: the hand-off page, told why it is
    /// being shown and where to put them back afterwards.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Login"/>. That is a challenge, and a challenge is the
    /// wrong answer to a session that has just ended: it sends the person through Keycloak,
    /// signs them back in, and only then lands them on a page telling them their session
    /// expired -- so they sign in twice and are told the opposite of what just happened.
    /// The hand-off is a page. It explains itself and asks the "Keep me signed in" question
    /// before the round trip rather than after it.
    /// </remarks>
    /// <param name="reason">One of <see cref="HandoffReason"/>.</param>
    public static string Handoff(string reason, string? returnUrl = null) =>
        WithReturnUrl(
            Append(HandoffPath, $"reason={Uri.EscapeDataString(reason)}"),
            returnUrl);

    /// <summary>
    /// Appends <paramref name="returnUrl"/> when it is a same-site path. The
    /// server validates it again before honouring it.
    /// </summary>
    private static string WithReturnUrl(string path, string? returnUrl)
    {
        if (!LocalUrl.IsLocal(returnUrl))
        {
            return path;
        }

        return Append(path, $"returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    private static string Append(string path, string pair) =>
        $"{path}{(path.Contains('?') ? '&' : '?')}{pair}";
}

/// <summary>
/// Why the sign-in hand-off is being shown, as it travels in the URL.
/// </summary>
/// <remarks>
/// A code rather than the sentence itself. A message carried in the query string is a
/// message anybody can choose, and the hand-off would render it: "your account is locked,
/// call this number", on the real sign-in page of the real app. The URL says which of our
/// sentences applies; <see cref="ErrorMessages"/> still owns the wording.
/// </remarks>
public static class HandoffReason
{
    /// <summary>The session behind the cookie is gone, and the API answered 401.</summary>
    public const string SessionExpired = "session-expired";

    /// <summary>
    /// What to show for <paramref name="reason"/>, or null when there is nothing to say --
    /// an ordinary visit to the sign-in page, or a code this version does not know.
    /// </summary>
    public static string? Message(string? reason) => reason switch
    {
        SessionExpired => ErrorMessages.SessionExpired,
        _ => null
    };
}

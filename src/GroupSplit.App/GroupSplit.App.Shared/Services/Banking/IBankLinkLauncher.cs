namespace GroupSplit.App.Shared.Services.Banking;

/// <summary>
/// The provider's own linking UI, from the application's side of it.
/// </summary>
/// <remarks>
/// The client half of the seam <c>IBankConnector</c> is on the server. Everything above
/// this speaks about linking a bank; only the implementation knows whose UI is opening,
/// which script it fetches, or what it calls its tokens.
/// <para>
/// An interface rather than the concrete launcher because of what the concrete one does:
/// it opens somebody else's UI and waits for a callback only a browser can make. A
/// component holding it directly cannot be rendered in a test without that wait becoming a
/// hang, so the whole of the link flow — the part of the app that spends real money at a
/// provider — was untestable past the token.
/// </para>
/// </remarks>
public interface IBankLinkLauncher
{
    /// <summary>
    /// Shows the linking UI. Answers the public token when somebody links a bank, and null
    /// when they close it, when it fails, or when the browser cannot run it at all.
    /// </summary>
    /// <param name="connectionId">
    /// The connection being repaired, when this is update mode. Written down with the
    /// token, because an OAuth institution takes the browser away and whatever comes back
    /// has to know which of the two things it was doing.
    /// </param>
    Task<string?> OpenAsync(string linkToken, Guid? connectionId = null);

    /// <summary>
    /// Picks up a session an OAuth institution interrupted, on the page it returned to.
    /// </summary>
    Task<string?> ResumeAsync(string linkToken);

    /// <summary>The link this tab had in flight, or null if there is none to pick up.</summary>
    Task<PendingBankLinkSession?> PendingAsync();
}

/// <summary>
/// A link this tab started and has not finished, held across the trip to a bank's own
/// sign-in page.
/// </summary>
/// <param name="ConnectionId">
/// Null for a fresh link. Set when the session was repairing a connection, which ends in a
/// refresh rather than an exchange.
/// </param>
public sealed record PendingBankLinkSession(string Token, Guid? ConnectionId);

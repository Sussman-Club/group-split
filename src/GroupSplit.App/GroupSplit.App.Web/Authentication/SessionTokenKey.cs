using System.Security.Claims;

namespace GroupSplit.App.Web.Authentication;

/// <summary>
/// Names the one sign-in session a set of tokens belongs to.
/// </summary>
/// <remarks>
/// The token store is keyed by principal, which is what lets a circuit read it. A principal
/// still has to say which of a person's sessions it is, or two browsers signed in as the
/// same person share an entry and take turns retiring each other's refresh token. Keycloak's
/// <c>sid</c> would do where it is issued, and it is not issued in every realm
/// configuration, so a claim of our own is minted at sign-in instead.
/// </remarks>
internal static class SessionTokenKey
{
    /// <summary>Not a name any authority would send, so a claim from a token cannot pose as it.</summary>
    private const string ClaimType = "groupsplit:sign_in_session";

    private const string CachePrefix = "gs-user-tokens:";

    internal static string Mint(ClaimsIdentity identity)
    {
        var sessionId = Guid.NewGuid().ToString("N");

        identity.AddClaim(new Claim(ClaimType, sessionId));

        return sessionId;
    }

    /// <summary>
    /// Where this principal's tokens live, or <c>null</c> when it carries no session
    /// identifier -- a ticket from before this claim existed, which is a reason to sign in
    /// again rather than to guess a key.
    /// </summary>
    internal static string? For(ClaimsPrincipal? principal)
    {
        var sessionId = principal?.FindFirst(ClaimType)?.Value;

        return string.IsNullOrWhiteSpace(sessionId) ? null : CachePrefix + sessionId;
    }
}

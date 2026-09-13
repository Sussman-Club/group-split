namespace GroupSplit.App.Shared.Services;

/// <summary>
/// The BFF endpoints that start and end a Keycloak session, shared by the web
/// and WebAssembly auth services so the two cannot drift apart.
/// </summary>
public static class AuthRoutes
{
    public const string Logout = "/auth/logout";

    /// <param name="remember">
    /// Whether "Keep me signed in" was ticked. Passed rather than remembered client-side:
    /// the cookie it decides is written by the server, at the end of a round trip through
    /// Keycloak that this page does not survive.
    /// </param>
    public static string Login(string? returnUrl = null, bool remember = false)
    {
        var login = WithReturnUrl("/auth/login", returnUrl);

        return remember
            ? $"{login}{(login.Contains('?') ? '&' : '?')}remember=true"
            : login;
    }

    /// <summary>
    /// Appends <paramref name="returnUrl"/> when it is a same-site path. The
    /// server validates it again before honouring it.
    /// </summary>
    private static string WithReturnUrl(string path, string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return path;
        }

        return $"{path}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }
}

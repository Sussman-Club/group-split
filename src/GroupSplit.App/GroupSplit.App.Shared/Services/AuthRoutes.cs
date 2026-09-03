namespace GroupSplit.App.Shared.Services;

/// <summary>
/// The BFF endpoints that start and end a Keycloak session, shared by the web
/// and WebAssembly auth services so the two cannot drift apart.
/// </summary>
public static class AuthRoutes
{
    public const string Logout = "/auth/logout";

    public static string Login(string? returnUrl = null) => WithReturnUrl("/auth/login", returnUrl);

    public static string Register(string? returnUrl = null) => WithReturnUrl("/auth/register", returnUrl);

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

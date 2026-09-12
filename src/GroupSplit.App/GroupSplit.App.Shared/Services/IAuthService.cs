namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Starts the Keycloak-hosted flows. Each call is a navigation that does not return.
/// </summary>
public interface IAuthService
{
    /// <param name="returnUrl">Local path to land on afterwards.</param>
    /// <param name="remember">Whether the session should survive the browser closing.</param>
    Task Login(string? returnUrl = null, bool remember = false, CancellationToken ct = default);

    Task Logout(CancellationToken ct = default);
}

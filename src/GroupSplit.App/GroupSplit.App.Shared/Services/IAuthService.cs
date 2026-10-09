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

    /// <summary>
    /// Signs out of this app only, once the account has been deleted. The login and its
    /// Keycloak sessions were deleted with the account, so there is nothing to end there.
    /// </summary>
    Task SignOutDeletedAccount(CancellationToken ct = default);
}

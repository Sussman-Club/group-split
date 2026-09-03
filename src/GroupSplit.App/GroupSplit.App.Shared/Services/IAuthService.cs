namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Starts the Keycloak-hosted flows. Each call is a navigation that does not return.
/// </summary>
public interface IAuthService
{
    /// <param name="returnUrl">Local path to land on afterwards.</param>
    Task Login(string? returnUrl = null, CancellationToken ct = default);

    Task Logout(CancellationToken ct = default);
}

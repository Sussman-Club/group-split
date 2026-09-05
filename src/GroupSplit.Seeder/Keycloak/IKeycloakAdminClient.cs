namespace GroupSplit.Seeder.Keycloak;

/// <summary>
/// The slice of Keycloak's Admin REST API the seeder needs. An interface so the seeder's
/// decisions -- what to skip, what to replace -- can be tested without a Keycloak.
/// </summary>
public interface IKeycloakAdminClient
{
    /// <summary>The realm the demo accounts belong to, for logging.</summary>
    string Realm { get; }

    /// <summary>
    /// Signs in as the bootstrap admin. Called once before any account is touched, so bad
    /// credentials are reported as such rather than as a failure on the first account.
    /// </summary>
    Task SignInAsync(CancellationToken ct = default);

    /// <summary>The user with this id, or null when the realm has none.</summary>
    Task<KeycloakUserSummary?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>The user holding this email, or null.</summary>
    Task<KeycloakUserSummary?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Creates the account, keeping the id it carries.</summary>
    Task CreateAsync(KeycloakUser user, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);
}

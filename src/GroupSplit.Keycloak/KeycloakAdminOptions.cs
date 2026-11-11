namespace GroupSplit.Keycloak;

/// <summary>
/// How a caller of Keycloak's Admin REST API signs in: as a user, with the password grant, or
/// as a confidential client's service account, with the client-credentials grant.
/// </summary>
/// <remarks>
/// The realm workers and the seeder sign in as the bootstrap admin of the master realm. The API
/// signs in as <see cref="KeycloakRealmDefinition.AccountDeletionClientId"/>, a client in the
/// app's own realm whose service account holds <c>manage-users</c> and nothing else -- so its
/// <see cref="AdminRealm"/> is <c>group-split</c>, and <see cref="AdminClientSecret"/> is what
/// selects the grant.
/// </remarks>
public class KeycloakAdminOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>The realm the token is requested from. The admin calls themselves always name the app's realm.</summary>
    public string AdminRealm { get; init; } = "master";

    public string AdminClientId { get; init; } = "admin-cli";

    /// <summary>
    /// The confidential client's secret. When set, the client signs in as itself with the
    /// client-credentials grant, and <see cref="AdminUser"/> and <see cref="AdminPassword"/>
    /// are not used.
    /// </summary>
    public string? AdminClientSecret { get; init; }

    public string? AdminUser { get; init; }

    public string? AdminPassword { get; init; }

    /// <summary>
    /// The path Keycloak is served under, or empty when it is served at the root. Has to match
    /// the server's <c>KC_HTTP_RELATIVE_PATH</c>: deployed, Keycloak answers only below
    /// <c>/idp</c>, and a worker calling <c>/realms/master/...</c> without it is told 404 by
    /// every endpoint it needs.
    /// </summary>
    public string? RelativePath { get; init; }

    /// <summary>The base address for admin calls: the service address, the relative path, and a trailing slash.</summary>
    public Uri BaseAddressFor(string service)
    {
        var root = service.TrimEnd('/');
        var path = (RelativePath ?? "").Trim('/');

        return new Uri(path.Length == 0 ? $"{root}/" : $"{root}/{path}/");
    }

    /// <summary>Whether the caller signs in as a client's service account rather than as a user.</summary>
    public bool UsesClientCredentials => !string.IsNullOrWhiteSpace(AdminClientSecret);

    public bool IsConfigured =>
        UsesClientCredentials
        || (!string.IsNullOrWhiteSpace(AdminUser) && !string.IsNullOrWhiteSpace(AdminPassword));
}

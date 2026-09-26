namespace GroupSplit.Keycloak;

/// <summary>Bootstrap admin credentials used for Keycloak's Admin REST API.</summary>
public class KeycloakAdminOptions
{
    public const string SectionName = "Keycloak";

    public string AdminRealm { get; init; } = "master";

    public string AdminClientId { get; init; } = "admin-cli";

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

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AdminUser) && !string.IsNullOrWhiteSpace(AdminPassword);
}

namespace GroupSplit.Keycloak;

/// <summary>Bootstrap admin credentials used for Keycloak's Admin REST API.</summary>
public class KeycloakAdminOptions
{
    public const string SectionName = "Keycloak";

    public string AdminRealm { get; init; } = "master";

    public string AdminClientId { get; init; } = "admin-cli";

    public string? AdminUser { get; init; }

    public string? AdminPassword { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AdminUser) && !string.IsNullOrWhiteSpace(AdminPassword);
}

using System.Text.Json.Nodes;

namespace GroupSplit.Keycloak;

/// <summary>
/// Reads the same realm export Keycloak imports on first boot. The allowlist defines the
/// realm-level properties the application owns; clients, flows, identity providers, users,
/// and environment-substituted SMTP settings are intentionally outside reconciliation.
/// </summary>
public static class KeycloakRealmDefinition
{
    private const string ResourceName = "GroupSplit.Keycloak.Assets.realms.json";

    public static string RealmName => Load()["realm"]!.GetValue<string>();

    public static readonly string[] ManagedSettings =
    [
        "displayName",
        "displayNameHtml",
        "enabled",
        "loginTheme",
        "accountTheme",
        "emailTheme",
        "registrationAllowed",
        "registrationEmailAsUsername",
        "loginWithEmailAllowed",
        "duplicateEmailsAllowed",
        "resetPasswordAllowed",
        "rememberMe",
        "editUsernameAllowed",
        "verifyEmail",
        "passwordPolicy",
        "bruteForceProtected",
        "permanentLockout",
        "failureFactor",
        "waitIncrementSeconds",
        "maxFailureWaitSeconds",
        "minimumQuickLoginWaitSeconds",
        "quickLoginCheckMilliSeconds",
        "maxDeltaTimeSeconds",
        "accessTokenLifespan",
        "ssoSessionIdleTimeout",
        "ssoSessionMaxLifespan",
        "accessCodeLifespan",
        "actionTokenGeneratedByUserLifespan"
    ];

    public static JsonObject Load()
    {
        using var stream = typeof(KeycloakRealmDefinition).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded Keycloak realm definition {ResourceName} was not found.");

        var definition = JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException("The Keycloak realm definition must be a JSON object.");

        if (definition["realm"] is not JsonValue realm || !realm.TryGetValue<string>(out var realmName)
            || string.IsNullOrWhiteSpace(realmName))
        {
            throw new InvalidOperationException("The Keycloak realm definition must name its realm.");
        }

        foreach (var name in ManagedSettings)
        {
            if (!definition.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"The Keycloak realm definition is missing managed setting '{name}'.");
            }
        }

        return definition;
    }
}

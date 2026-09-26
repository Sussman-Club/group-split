using System.Text.Json.Nodes;

namespace GroupSplit.Keycloak;

/// <summary>
/// Reads the same realm export Keycloak imports on first boot. The allowlists define what
/// the application owns: realm-level properties, and the handful of client properties that
/// decide where a sign-in may be sent back to.
/// Everything else -- the rest of each client, protocol mappers, flows, identity providers,
/// roles, users, and the environment-substituted SMTP settings -- is intentionally outside
/// reconciliation.
/// </summary>
public static class KeycloakRealmDefinition
{
    private const string ResourceName = "GroupSplit.Keycloak.Assets.realms.json";

    public static string RealmName => Load()["realm"]!.GetValue<string>();

    /// <summary>
    /// Client properties reconciled onto an existing realm, by <c>clientId</c>.
    /// </summary>
    /// <remarks>
    /// Only where a client may redirect to. That is the part of a client whose drift is a
    /// vulnerability rather than an inconvenience -- a realm created while <c>web-app</c>
    /// accepted any redirect URI kept accepting them long after the file stopped saying so
    /// -- and it is also the part that differs by environment, so it is the part a first-boot
    /// import gets wrong the moment the web app moves. Everything else about a client stays
    /// with the import.
    /// </remarks>
    public static readonly ManagedClient[] ManagedClients =
    [
        new("web-app", Properties: ["redirectUris", "webOrigins"], Attributes: ["post.logout.redirect.uris"])
    ];


    /// <summary>The client in <paramref name="definition"/> with this <c>clientId</c>, or null.</summary>
    public static JsonObject? Client(JsonObject definition, string clientId) =>
        definition["clients"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(client => client["clientId"]?.GetValue<string>() == clientId);

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

        foreach (var managed in ManagedClients)
        {
            var client = Client(definition, managed.ClientId)
                ?? throw new InvalidOperationException(
                    $"The Keycloak realm definition has no client '{managed.ClientId}'.");

            foreach (var name in managed.Properties.Where(name => !client.ContainsKey(name)))
            {
                throw new InvalidOperationException(
                    $"Client '{managed.ClientId}' in the Keycloak realm definition is missing managed property '{name}'.");
            }

            var attributes = client["attributes"] as JsonObject;

            foreach (var name in managed.Attributes.Where(name => attributes?.ContainsKey(name) != true))
            {
                throw new InvalidOperationException(
                    $"Client '{managed.ClientId}' in the Keycloak realm definition is missing managed attribute '{name}'.");
            }
        }

        return definition;
    }
}

/// <summary>A client whose listed properties and attributes are reconciled onto an existing realm.</summary>
public sealed record ManagedClient(string ClientId, string[] Properties, string[] Attributes);

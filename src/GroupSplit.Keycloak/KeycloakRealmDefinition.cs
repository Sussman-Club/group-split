using System.Text.Json.Nodes;

namespace GroupSplit.Keycloak;

/// <summary>
/// Reads the same realm export Keycloak imports on first boot. The allowlists define what
/// the application owns: realm-level properties, including which flow runs each kind of
/// sign-in; the handful of client properties that decide where a sign-in may be sent back
/// to and which grants a client accepts; the identity provider settings and first broker
/// login flow that decide which account a brokered sign-in may land in; and, whole, the
/// confidential clients the application itself signs in as (<see cref="ServiceClients"/>).
/// Everything else -- the rest of each client and provider, the built-in flows themselves,
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
    /// <para>
    /// For <c>web-app</c>, only where it may redirect to. That is the part of a client whose
    /// drift is a vulnerability rather than an inconvenience -- a realm created while
    /// <c>web-app</c> accepted any redirect URI kept accepting them long after the file stopped
    /// saying so -- and it is also the part that differs by environment, so it is the part a
    /// first-boot import gets wrong the moment the web app moves.
    /// </para>
    /// <para>
    /// For <c>cli</c>, which grants it accepts and the audience its tokens carry: the device
    /// grant and nothing else, and <c>api</c>, without which the API refuses every token the
    /// CLI brings. Unlike <c>web-app</c> it is created when missing. A realm imported before
    /// the CLI existed has no <c>cli</c> client, and nothing in the client depends on the
    /// environment, so the file's definition is all there is to it.
    /// </para>
    /// <para>
    /// Everything else about a client stays with the import.
    /// </para>
    /// </remarks>
    public static readonly ManagedClient[] ManagedClients =
    [
        new("web-app", Properties: ["redirectUris", "webOrigins"], Attributes: ["post.logout.redirect.uris"]),
        new("cli",
            Properties:
            [
                "publicClient",
                "standardFlowEnabled",
                "implicitFlowEnabled",
                "directAccessGrantsEnabled",
                "serviceAccountsEnabled"
            ],
            Attributes: ["oauth2.device.authorization.grant.enabled", "oidc.ciba.grant.enabled"])
        {
            ProtocolMappers = ["audience-mapper"],
            CreateWhenMissing = true
        }
    ];

    /// <summary>The confidential client the API deletes a deleted account's login through.</summary>
    public const string AccountDeletionClientId = "account-deletion";

    /// <summary>
    /// The client role container a service account's admin permissions come from.
    /// </summary>
    public const string RealmManagementClientId = "realm-management";

    /// <summary>
    /// Confidential clients the application signs in as, owned whole by the realm worker: created
    /// on a realm that lacks them, held to the file's flows, held to the secret the deployment
    /// gives them, and held to exactly the file's roles.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ManagedClients"/>, which the worker refuses to create. A client whose
    /// secret is a deploy secret has to be written by something that runs on every deploy --
    /// an import that ran once, years ago, knows neither the secret it is rotated to nor that
    /// the client was ever added -- and creating one needs nothing the worker is not already
    /// given. The roles are held exactly rather than at least, because the whole point of a
    /// narrow client is that it stays narrow: a role somebody added by hand is revoked.
    /// </remarks>
    public static readonly ServiceClient[] ServiceClients =
    [
        new(AccountDeletionClientId, SecretVariable: "KEYCLOAK_ACCOUNT_DELETION_SECRET")
    ];

    /// <summary>
    /// The client properties a <see cref="ServiceClient"/> is held to. Together they say: a
    /// confidential client that can only sign in as itself.
    /// </summary>
    public static readonly string[] ServiceClientProperties =
    [
        "enabled",
        "publicClient",
        "clientAuthenticatorType",
        "standardFlowEnabled",
        "implicitFlowEnabled",
        "directAccessGrantsEnabled",
        "serviceAccountsEnabled"
    ];

    /// <summary>
    /// The <see cref="RealmManagementClientId"/> roles the file grants the service account of
    /// <paramref name="clientId"/>, in the order it lists them.
    /// </summary>
    public static IReadOnlyList<string> ServiceAccountRoles(JsonObject definition, string clientId) =>
        definition["users"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(user => user["serviceAccountClientId"]?.GetValue<string>() == clientId)
            ?["clientRoles"]?[RealmManagementClientId]?.AsArray()
            .Select(role => role!.GetValue<string>())
            .ToList()
        ?? [];


    /// <summary>
    /// Identity provider settings reconciled onto an existing realm, by alias -- together with
    /// the first broker login flow each one names, which is reconciled whole.
    /// </summary>
    /// <remarks>
    /// The part of a provider that decides which account a stranger's sign-in lands in. A
    /// realm imported while that flow showed a profile form before auto-linking let someone
    /// type another person's address into it and inherit their account, and the import never
    /// ran again to take the form away. Not <c>enabled</c>, the client id or the secret: those
    /// are the container's, from variables this worker is not given.
    /// </remarks>
    public static readonly ManagedIdentityProvider[] ManagedIdentityProviders =
    [
        new("google",
            Properties: ["trustEmail", "firstBrokerLoginFlowAlias"],
            Config: ["filteredByClaim", "claimFilterName", "claimFilterValue"])
    ];

    /// <summary>The client in <paramref name="definition"/> with this <c>clientId</c>, or null.</summary>
    public static JsonObject? Client(JsonObject definition, string clientId) =>
        definition["clients"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(client => client["clientId"]?.GetValue<string>() == clientId);

    /// <summary>The protocol mapper of <paramref name="client"/> with this name, or null.</summary>
    public static JsonObject? ProtocolMapper(JsonObject client, string name) =>
        client["protocolMappers"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(mapper => mapper["name"]?.GetValue<string>() == name);

    /// <summary>The identity provider in <paramref name="definition"/> with this alias, or null.</summary>
    public static JsonObject? IdentityProvider(JsonObject definition, string alias) =>
        definition["identityProviders"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(provider => provider["alias"]?.GetValue<string>() == alias);

    /// <summary>The authentication flow in <paramref name="definition"/> with this alias, or null.</summary>
    public static JsonObject? Flow(JsonObject definition, string alias) =>
        definition["authenticationFlows"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(flow => flow["alias"]?.GetValue<string>() == alias);

    /// <summary>
    /// The authenticators of a flow in the order Keycloak runs them, each with its requirement.
    /// </summary>
    public static IReadOnlyList<FlowStep> Steps(JsonObject flow) =>
        flow["authenticationExecutions"]!.AsArray()
            .OfType<JsonObject>()
            .OrderBy(execution => execution["priority"]?.GetValue<int>() ?? 0)
            .Select(execution => new FlowStep(
                execution["authenticator"]!.GetValue<string>(),
                execution["requirement"]!.GetValue<string>()))
            .ToList();

    /// <summary>Realm-level properties reconciled onto an existing realm.</summary>
    /// <remarks>
    /// The flow bindings name Keycloak's built-in flows, which the file leaves to Keycloak to
    /// create. They are stated anyway, because a binding is what decides which flow a sign-in
    /// runs: a browser flow re-pointed at a weaker copy, by hand or by a mistaken import,
    /// would otherwise go unnoticed. Only which flow each binding names is reconciled, not
    /// the built-in flows themselves.
    /// </remarks>
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
        "actionTokenGeneratedByUserLifespan",
        "browserFlow",
        "registrationFlow",
        "directGrantFlow",
        "resetCredentialsFlow",
        "clientAuthenticationFlow",
        "firstBrokerLoginFlow"
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

            foreach (var name in managed.ProtocolMappers.Where(name => ProtocolMapper(client, name) is null))
            {
                throw new InvalidOperationException(
                    $"Client '{managed.ClientId}' in the Keycloak realm definition is missing managed protocol mapper '{name}'.");
            }
        }

        foreach (var service in ServiceClients)
        {
            var client = Client(definition, service.ClientId)
                ?? throw new InvalidOperationException(
                    $"The Keycloak realm definition has no client '{service.ClientId}'.");

            foreach (var name in ServiceClientProperties.Where(name => !client.ContainsKey(name)))
            {
                throw new InvalidOperationException(
                    $"Client '{service.ClientId}' in the Keycloak realm definition is missing property '{name}'.");
            }

            if (client["secret"]?.GetValue<string>() != $"${{{service.SecretVariable}}}")
            {
                throw new InvalidOperationException(
                    $"Client '{service.ClientId}' in the Keycloak realm definition must take its secret from "
                    + $"${{{service.SecretVariable}}}, the variable the realm worker holds it to.");
            }

            if (ServiceAccountRoles(definition, service.ClientId).Count == 0)
            {
                throw new InvalidOperationException(
                    $"The Keycloak realm definition grants the service account of '{service.ClientId}' no "
                    + $"{RealmManagementClientId} roles.");
            }
        }

        foreach (var managed in ManagedIdentityProviders)
        {
            var provider = IdentityProvider(definition, managed.Alias)
                ?? throw new InvalidOperationException(
                    $"The Keycloak realm definition has no identity provider '{managed.Alias}'.");

            foreach (var name in managed.Properties.Where(name => !provider.ContainsKey(name)))
            {
                throw new InvalidOperationException(
                    $"Identity provider '{managed.Alias}' in the Keycloak realm definition is missing managed property '{name}'.");
            }

            var config = provider["config"] as JsonObject;

            foreach (var name in managed.Config.Where(name => config?.ContainsKey(name) != true))
            {
                throw new InvalidOperationException(
                    $"Identity provider '{managed.Alias}' in the Keycloak realm definition is missing managed config '{name}'.");
            }

            ValidateManagedFlow(definition, provider["firstBrokerLoginFlowAlias"]!.GetValue<string>());
        }

        return definition;
    }

    /// <summary>
    /// A reconciled flow is a flat list of authenticators and nothing else. Subflows and
    /// authenticator configs are what the import is for; this worker would have to become a
    /// second importer to keep them in step.
    /// </summary>
    private static void ValidateManagedFlow(JsonObject definition, string alias)
    {
        var flow = Flow(definition, alias)
            ?? throw new InvalidOperationException(
                $"The Keycloak realm definition has no authentication flow '{alias}'.");

        if (flow["topLevel"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException($"Authentication flow '{alias}' must be a top-level flow.");
        }

        foreach (var execution in flow["authenticationExecutions"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (execution["authenticator"] is null || execution["requirement"] is null
                || execution.ContainsKey("flowAlias") || execution.ContainsKey("authenticatorConfig"))
            {
                throw new InvalidOperationException(
                    $"Authentication flow '{alias}' is reconciled onto existing realms, so it may hold only "
                    + "authenticators with a requirement: no subflows and no authenticator configs.");
            }
        }
    }
}

/// <summary>A client whose listed properties and attributes are reconciled onto an existing realm.</summary>
public sealed record ManagedClient(string ClientId, string[] Properties, string[] Attributes)
{
    /// <summary>
    /// Protocol mappers reconciled by name: created when missing, and given the file's mapper
    /// type and config values when they differ. Config entries the file does not name are kept.
    /// </summary>
    public string[] ProtocolMappers { get; init; } = [];

    /// <summary>
    /// Whether a realm without the client is given it, from the file's whole definition,
    /// rather than reported. Only for a client none of whose definition depends on the
    /// environment, so that creating it cannot get anything wrong that an import would not.
    /// </summary>
    public bool CreateWhenMissing { get; init; }
}

/// <summary>
/// A confidential client the application signs in as, whose secret the deployment supplies in
/// <paramref name="SecretVariable"/> -- the same name the file's <c>${...}</c> placeholder uses.
/// </summary>
public sealed record ServiceClient(string ClientId, string SecretVariable);

/// <summary>
/// An identity provider whose listed properties and config entries are reconciled onto an
/// existing realm, along with its first broker login flow.
/// </summary>
public sealed record ManagedIdentityProvider(string Alias, string[] Properties, string[] Config);

/// <summary>One authenticator of a flow and its requirement, e.g. <c>REQUIRED</c>.</summary>
public sealed record FlowStep(string Authenticator, string Requirement)
{
    public override string ToString() => $"{Authenticator} {Requirement}";
}

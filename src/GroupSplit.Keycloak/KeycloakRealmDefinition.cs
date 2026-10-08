using System.Text.Json.Nodes;

namespace GroupSplit.Keycloak;

/// <summary>
/// Reads the same realm export Keycloak imports on first boot. The allowlists define what
/// the application owns: realm-level properties, the handful of client properties that
/// decide where a sign-in may be sent back to, and the identity provider settings and first
/// broker login flow that decide which account a brokered sign-in may land in.
/// Everything else -- the rest of each client and provider, protocol mappers, other flows,
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
public sealed record ManagedClient(string ClientId, string[] Properties, string[] Attributes);

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

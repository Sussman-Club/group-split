using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Configuration;

/// <summary>
/// Turns one server URL into the API base and the Keycloak realm, applying the usual
/// precedence: an explicit flag beats the environment, which beats the config file.
/// <para>
/// Nothing here is baked in at compile time. That is the point: the same binary talks to
/// a local Aspire run, a staging box and production, and which one is a runtime decision.
/// </para>
/// </summary>
public sealed class EndpointResolver(ConfigStore store)
{
    /// <summary>
    /// The path the web app forwards to the API on. Mirrors the <c>MapGroup("/api")</c>
    /// forwarder in <c>GroupSplit.App.Web.WebAppExtensions</c>: in a deployment the API
    /// has no public origin of its own and is only reachable through the web app.
    /// </summary>
    private const string ApiPath = "/api";

    /// <summary>
    /// Mirrors <c>KeycloakDeploymentExtensions.RelativePath</c> and the realm name the API
    /// validates against in <c>GroupSplit.API.Program</c>. Change either and this follows.
    /// </summary>
    private const string RealmPath = "/idp/realms/group-split";

    public const string DefaultClientId = "cli";

    public Endpoints Resolve(string? serverOption, string? profileOption)
    {
        var config = store.Load();

        var profileName = profileOption
                          ?? Environment.GetEnvironmentVariable(EnvironmentVariables.Profile)
                          ?? config.DefaultProfile;

        config.Profiles.TryGetValue(profileName, out var profile);

        if (profileOption is not null && profile is null)
        {
            throw CliException.Input(
                $"No profile named '{profileName}' in {store.Path}.",
                config.Profiles.Count == 0
                    ? "Create one with: groupsplit config set server <url> --profile " + profileName
                    : "Known profiles: " + string.Join(", ", config.Profiles.Keys));
        }

        var (server, source) = FirstSet(
            (serverOption, "--server"),
            (Environment.GetEnvironmentVariable(EnvironmentVariables.Server), EnvironmentVariables.Server),
            (profile?.Server, $"{store.Path} (profile '{profileName}')"));

        var apiOverride = Environment.GetEnvironmentVariable(EnvironmentVariables.ApiUrl) ?? profile?.ApiUrl;
        var authorityOverride = Environment.GetEnvironmentVariable(EnvironmentVariables.Authority) ?? profile?.Authority;

        // Only the API is required here. The authority is resolved to null when nothing
        // supplies one and only complained about if a command actually needs to sign in,
        // so `GROUPSPLIT_API_URL` plus `GROUPSPLIT_TOKEN` is a complete configuration.
        if (server is null && apiOverride is null)
        {
            // Exit code 3 rather than 1: retrying will not help, the invocation or the
            // environment has to change first, and that is the distinction 3 carries.
            throw CliException.Input(
                "No server configured.",
                $"Set one with: groupsplit config set server <url>   (or pass --server, or set {EnvironmentVariables.Server})",
                code: ErrorCodes.ServerNotConfigured);
        }

        var origin = server is null ? null : ParseAbsolute(server, "--server");

        return new Endpoints
        {
            Api = apiOverride is not null
                ? ParseAbsolute(apiOverride, EnvironmentVariables.ApiUrl)
                : Combine(origin!, ApiPath),
            AuthorityOrNull = authorityOverride is not null
                ? ParseAbsolute(authorityOverride, EnvironmentVariables.Authority)
                : origin is null
                    ? null
                    : Combine(origin, RealmPath),
            ClientId = Environment.GetEnvironmentVariable(EnvironmentVariables.ClientId)
                       ?? profile?.ClientId
                       ?? DefaultClientId,
            ProfileName = profileName,
            Source = server is null ? EnvironmentVariables.ApiUrl : source!
        };
    }

    private static (string? Value, string? Source) FirstSet(params (string? Value, string Source)[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Value))
            {
                return candidate;
            }
        }

        return (null, null);
    }

    private static Uri ParseAbsolute(string value, string source)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw CliException.Input(
                $"'{value}' from {source} is not an absolute http or https URL.",
                "Include the scheme, e.g. https://groupsplit.example.com");
        }

        return uri;
    }

    /// <summary>
    /// Appends rather than replaces, so a server hosted under a sub-path keeps it. Uri's
    /// own relative resolution would discard everything after the origin.
    /// </summary>
    private static Uri Combine(Uri origin, string path)
        => new($"{origin.GetLeftPart(UriPartial.Path).TrimEnd('/')}{path}");
}

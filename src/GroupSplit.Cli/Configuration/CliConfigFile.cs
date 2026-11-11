using System.Text.Json.Serialization;

namespace GroupSplit.Cli.Configuration;

/// <summary>
/// A profile names one deployment. Having more than one is the normal case -- a local
/// Aspire stack and the deployed one -- and switching with --profile beats re-typing a
/// URL that is easy to get subtly wrong.
/// </summary>
public sealed class CliProfile
{
    /// <summary>
    /// The public origin, e.g. <c>https://groupsplit.example.com</c>. The API and Keycloak
    /// are both derived from it, which is what makes a deployment one setting rather than three.
    /// </summary>
    [JsonPropertyName("server")]
    public string? Server { get; set; }

    /// <summary>
    /// Overrides the derived API base. Needed when the API is not behind the web app's
    /// forwarder -- a local Aspire run gives each resource its own origin and port.
    /// </summary>
    [JsonPropertyName("apiUrl")]
    public string? ApiUrl { get; set; }

    /// <summary>Overrides the derived Keycloak realm URL, for the same reason as <see cref="ApiUrl"/>.</summary>
    [JsonPropertyName("authority")]
    public string? Authority { get; set; }

    /// <summary>The Keycloak client this CLI authenticates as. Only set it if the realm was renamed.</summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}

public sealed class CliConfigFile
{
    [JsonPropertyName("defaultProfile")]
    public string DefaultProfile { get; set; } = "default";

    [JsonPropertyName("profiles")]
    public Dictionary<string, CliProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

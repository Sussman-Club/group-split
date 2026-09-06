using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Auth;

public sealed record OidcEndpoints
{
    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;

    [JsonPropertyName("device_authorization_endpoint")]
    public string? DeviceAuthorizationEndpoint { get; init; }

    [JsonPropertyName("end_session_endpoint")]
    public string? EndSessionEndpoint { get; init; }

    [JsonPropertyName("issuer")]
    public string Issuer { get; init; } = string.Empty;
}

/// <summary>
/// Reads the realm's discovery document rather than assembling Keycloak's URLs by hand.
/// The endpoints then come from the server that will actually be answering, so a realm
/// moved behind a different path keeps working without a CLI change.
/// </summary>
public sealed class OidcDiscovery(HttpClient http)
{
    public async Task<OidcEndpoints> GetAsync(Uri authority, CancellationToken ct)
    {
        var url = $"{authority.ToString().TrimEnd('/')}/.well-known/openid-configuration";

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw CliException.Failure(
                $"Could not reach the identity server at {authority}.",
                ErrorCodes.ServerUnreachable,
                "Check the server URL with: groupsplit config list",
                inner: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CliException.Failure(
                $"The identity server at {authority} returned {(int)response.StatusCode} for its discovery document.",
                ErrorCodes.ServerUnreachable,
                "Check that the URL points at a Keycloak realm, e.g. https://host/idp/realms/group-split");
        }

        var endpoints = await response.Content.ReadFromJsonAsync<OidcEndpoints>(ct);

        if (endpoints is null || string.IsNullOrEmpty(endpoints.TokenEndpoint))
        {
            throw CliException.Failure(
                $"The discovery document at {url} has no token endpoint.",
                ErrorCodes.ServerError);
        }

        return endpoints;
    }
}

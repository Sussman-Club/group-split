using System.Net.Http.Json;
using System.Text.Json;
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
/// <para>
/// Which is also why the document is not simply believed. It names the addresses this CLI
/// will post a device code and a refresh token to, so every one of them is required to sit
/// on the same origin as the authority the user configured. Nothing legitimate is turned
/// away by that -- Keycloak serves its endpoints under its own origin -- and it means a
/// tampered document cannot redirect credentials somewhere the user never named.
/// </para>
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

        OidcEndpoints? endpoints;
        try
        {
            endpoints = await response.Content.ReadFromJsonAsync<OidcEndpoints>(ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException is the content-type case: a proxy in front of Keycloak
            // answering with HTML is an ordinary production event, and without this it
            // escapes to the catch-all and claims the CLI has a bug.
            throw CliException.Failure(
                $"The response from {url} was not a discovery document.",
                ErrorCodes.ServerUnreachable,
                "Check that the URL points at a Keycloak realm and that nothing in front of it "
                + "is answering instead.",
                inner: ex);
        }

        if (endpoints is null || string.IsNullOrEmpty(endpoints.TokenEndpoint))
        {
            throw CliException.Failure(
                $"The discovery document at {url} has no token endpoint.",
                ErrorCodes.ServerError);
        }

        RequireSameOrigin(authority, endpoints.Issuer, "issuer");
        RequireSameOrigin(authority, endpoints.TokenEndpoint, "token_endpoint");
        RequireSameOrigin(authority, endpoints.DeviceAuthorizationEndpoint, "device_authorization_endpoint");
        RequireSameOrigin(authority, endpoints.EndSessionEndpoint, "end_session_endpoint");

        return endpoints;
    }

    /// <summary>
    /// Rejects a member naming somewhere other than the configured authority's origin. An
    /// https authority whose document named an http endpoint fails here too, since the
    /// scheme is part of the origin.
    /// </summary>
    private static void RequireSameOrigin(Uri authority, string? value, string member)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var target)
            || !string.Equals(
                target.GetLeftPart(UriPartial.Authority),
                authority.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            throw CliException.Auth(
                $"The identity server's {member} points at '{value}', which is not on "
                + $"{authority.GetLeftPart(UriPartial.Authority)}.",
                "Credentials are only ever sent to the server you configured. Check the "
                + "authority with: groupsplit config list",
                ErrorCodes.AuthFailed);
        }
    }
}

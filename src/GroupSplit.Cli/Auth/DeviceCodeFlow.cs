using System.Net.Http.Json;
using System.Text.Json;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Auth;

/// <summary>
/// OAuth 2.0 Device Authorization Grant, RFC 8628.
/// <para>
/// Chosen over the loopback authorization-code flow because a CLI is regularly run where
/// no browser can be opened and no port can be listened on -- over SSH, in a container,
/// on a build agent. The device flow degrades to "here is a URL and a code", which works
/// everywhere, and the browser that approves it need not be on the same machine.
/// </para>
/// </summary>
public sealed class DeviceCodeFlow(HttpClient http)
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>The shortest gap between polls, whatever the server asks for.</summary>
    private const int MinimumPollSeconds = 1;

    /// <summary>
    /// offline_access asks Keycloak for a refresh token that outlives the SSO session's
    /// idle timeout, so a CLI used twice a week does not sign in every time.
    /// </summary>
    private const string Scope = "openid profile email offline_access";

    public async Task<DeviceAuthorizationResponse> StartAsync(
        OidcEndpoints endpoints, string clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(endpoints.DeviceAuthorizationEndpoint))
        {
            throw CliException.Auth(
                "This realm does not advertise the device authorization endpoint.",
                "Enable 'OAuth 2.0 Device Authorization Grant' on the "
                + $"'{clientId}' client in Keycloak, then try again.",
                ErrorCodes.AuthFailed);
        }

        var response = await http.PostAsync(
            endpoints.DeviceAuthorizationEndpoint,
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("scope", Scope)
            ]),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            throw CliException.Auth(
                $"The identity server refused to start a device login ({(int)response.StatusCode}).",
                $"Check that the '{clientId}' client exists in the realm and has the device grant enabled.",
                ErrorCodes.AuthFailed);
        }

        return await ReadAsync<DeviceAuthorizationResponse>(response, "device authorization", ct);
    }

    /// <summary>
    /// Polls until the user approves, declines, or the code expires. Honours the server's
    /// interval and its slow_down, because polling faster than asked gets the request
    /// rejected outright rather than answered sooner.
    /// </summary>
    public async Task<TokenResponse> PollAsync(
        OidcEndpoints endpoints,
        string clientId,
        DeviceAuthorizationResponse device,
        CancellationToken ct)
    {
        // Floored at a second. The RFC's default is five when the server names none, but a
        // server that names zero -- broken, or hostile -- would otherwise turn this into a
        // tight loop hammering it for the whole lifetime of the code.
        var interval = TimeSpan.FromSeconds(Math.Max(MinimumPollSeconds, device.Interval ?? 5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn ?? 600);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);

            var token = await PostTokenAsync(endpoints, [
                new KeyValuePair<string, string>("grant_type", GrantType),
                new KeyValuePair<string, string>("device_code", device.DeviceCode),
                new KeyValuePair<string, string>("client_id", clientId)
            ], ct);

            switch (token.Error)
            {
                case null:
                    return token;

                case "authorization_pending":
                    continue;

                case "slow_down":
                    // RFC 8628 section 3.5: add five seconds and carry on.
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case "access_denied":
                    throw CliException.Auth(
                        "The sign-in request was denied.",
                        "Run `groupsplit auth login` again and approve the request.",
                        ErrorCodes.AuthFailed);

                case "expired_token":
                    throw CliException.Auth(
                        "The sign-in request expired before it was approved.",
                        "Run `groupsplit auth login` again.",
                        ErrorCodes.AuthFailed);

                default:
                    throw CliException.Auth(
                        token.ErrorDescription ?? $"The identity server returned '{token.Error}'.",
                        code: ErrorCodes.AuthFailed);
            }
        }

        throw CliException.Auth(
            "Timed out waiting for the sign-in to be approved.",
            "Run `groupsplit auth login` again.",
            ErrorCodes.AuthFailed);
    }

    public Task<TokenResponse> RefreshAsync(
        OidcEndpoints endpoints, string clientId, string refreshToken, CancellationToken ct)
        => PostTokenAsync(endpoints, [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("client_id", clientId)
        ], ct);

    /// <summary>Ends the Keycloak session so the refresh token cannot be used again.</summary>
    public async Task RevokeAsync(
        OidcEndpoints endpoints, string clientId, string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(endpoints.EndSessionEndpoint))
        {
            return;
        }

        try
        {
            await http.PostAsync(
                endpoints.EndSessionEndpoint,
                new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("refresh_token", refreshToken)
                ]),
                ct);
        }
        catch (HttpRequestException)
        {
            // Logging out locally is the part the user asked for and it has already
            // happened by the time this runs. An unreachable server must not fail it.
        }
    }

    private async Task<TokenResponse> PostTokenAsync(
        OidcEndpoints endpoints, KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        var response = await http.PostAsync(endpoints.TokenEndpoint, new FormUrlEncodedContent(form), ct);

        // The error cases are carried in the body as much as in the status, so both the
        // 200 and the 400 are parsed the same way.
        return await ReadAsync<TokenResponse>(response, "token", ct);
    }

    /// <summary>
    /// Reads a JSON body, or fails with something a reader can act on.
    /// <para>
    /// ReadFromJsonAsync throws NotSupportedException when the content type is not JSON, and
    /// a proxy in front of Keycloak answering with an HTML error page is an ordinary
    /// production event. Left alone it escapes to the catch-all and tells the user the CLI
    /// has a bug, which sends them looking in the wrong place entirely.
    /// </para>
    /// </summary>
    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, string what, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct)
                   ?? throw CliException.Auth($"The identity server returned an empty {what} response.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw CliException.Auth(
                $"The identity server's {what} response was not JSON "
                + $"({(int)response.StatusCode} {response.StatusCode}).",
                "Check that the authority points at a Keycloak realm and that nothing in "
                + "front of it is answering instead.",
                ErrorCodes.AuthFailed);
        }
    }
}

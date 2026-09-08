using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using GroupSplit.App.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GroupSplit.App.Web.Services;

/// <summary>
/// Keeps the access token in the sign-in ticket current, by exchanging the refresh token
/// while the cookie is being validated.
/// </summary>
/// <remarks>
/// The refresh happens in <c>OnValidatePrincipal</c> and nowhere else. It used to happen
/// wherever the token was first wanted, which in practice was inside a component's
/// <c>OnInitializedAsync</c> by way of the API client -- and persisting a refreshed token
/// means <c>SignInAsync</c>, which means a <c>Set-Cookie</c> header, which by then can no
/// longer be written: "Headers are read-only, response has already started". The exchange
/// itself succeeded every time, so the cost was not the exception in the log but the
/// refreshed token being dropped on the floor. The next request refreshed again, and where
/// the realm rotates refresh tokens one-time-use, each of those retired the stored one and
/// the person was signed out for no reason they could see.
/// <para>
/// The cookie handler runs this before anything has been written to the response, which is
/// the one point in a request where the ticket can still be rewritten -- so it is the only
/// place this belongs. Everything downstream reads the token the ticket already carries.
/// </para>
/// </remarks>
internal sealed class TokenRefreshService(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor,
    ILogger<TokenRefreshService> logger)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);

    /// <summary>How long a token is assumed good for when nothing says otherwise.</summary>
    private static readonly TimeSpan UnknownExpiryFloor = TimeSpan.FromMinutes(2);
    private static readonly JwtSecurityTokenHandler JwtTokenHandler = new();

    /// <summary>
    /// Validates the ticket on its way out of the cookie, refreshing the tokens it carries
    /// when they are due, and rejecting it when they cannot be refreshed.
    /// </summary>
    /// <remarks>
    /// Rejecting rather than returning quietly is what signs the person out: leaving a
    /// ticket in place whose tokens no longer work produces a session that looks signed in
    /// and gets a 401 from every call it makes.
    /// </remarks>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated is not true || context.Properties is null)
        {
            return;
        }

        var properties = context.Properties;
        var accessToken = properties.GetTokenValue(TokenNames.AccessToken);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            context.RejectPrincipal();
            return;
        }

        var options = openIdConnectOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);

        if (options.ConfigurationManager is null)
        {
            // Nothing to check the token against and nowhere to refresh it. Leaving the
            // ticket alone is the conservative answer: rejecting here would sign everybody
            // out the moment OIDC was misconfigured.
            return;
        }

        var cancellationToken = context.HttpContext.RequestAborted;

        OpenIdConnectConfiguration configuration;

        try
        {
            configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception e)
        {
            // Keycloak being briefly unreachable is not evidence that the ticket is bad.
            logger.LogWarning(e, "Could not read the OpenID Connect configuration; leaving the ticket as it is.");

            return;
        }

        var expectedIssuer = configuration.Issuer ?? options.Authority;

        // A token minted by an authority this app no longer talks to cannot be refreshed
        // into one that is, so there is nothing to do but start again.
        if (!HasExpectedIssuer(accessToken, expectedIssuer))
        {
            logger.LogInformation("The stored access token was issued by another authority; signing out.");
            context.RejectPrincipal();

            return;
        }

        if (!ShouldRefresh(properties.GetTokenValue(TokenNames.ExpiresAt)))
        {
            return;
        }

        if (await TryRefreshAsync(options, configuration, properties, cancellationToken))
        {
            // Rewrites the cookie, and the server-side ticket with it, before a single byte
            // of the response has been written.
            context.ShouldRenew = true;

            return;
        }

        logger.LogInformation("The access token could not be refreshed; signing out.");
        context.RejectPrincipal();
    }

    /// <summary>
    /// The access token the current ticket carries, or <c>null</c> when there is not a
    /// usable one.
    /// </summary>
    /// <remarks>
    /// A read, and only a read. <see cref="ValidateAsync"/> has already refreshed whatever
    /// needed refreshing by the time any of this app's own code runs, so there is nothing
    /// left to do here but hand the token over -- and nothing here writes to the response,
    /// which is what makes it safe to call from a component mid-render.
    /// </remarks>
    public async Task<string?> GetAccessTokenAsync(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated is not true)
        {
            return null;
        }

        var authResult = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (!authResult.Succeeded || authResult.Properties is null)
        {
            return null;
        }

        var accessToken = authResult.Properties.GetTokenValue(TokenNames.AccessToken);

        return string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
    }

    /// <summary>
    /// Exchanges the stored refresh token, writing the new tokens into
    /// <paramref name="properties"/>. Returns whether it worked.
    /// </summary>
    private async Task<bool> TryRefreshAsync(
        OpenIdConnectOptions options,
        OpenIdConnectConfiguration configuration,
        AuthenticationProperties properties,
        CancellationToken cancellationToken)
    {
        var currentRefreshToken = properties.GetTokenValue(TokenNames.RefreshToken);

        if (string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            return false;
        }

        var tokenEndpoint = configuration.TokenEndpoint;

        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Content = CreateRefreshContent(options, currentRefreshToken);

        var client = httpClientFactory.CreateClient();

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning(e, "The token endpoint could not be reached.");

            return false;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var refreshedAccessToken = GetRequiredString(payload.RootElement, TokenNames.AccessToken);

            if (string.IsNullOrWhiteSpace(refreshedAccessToken))
            {
                return false;
            }

            var refreshedRefreshToken =
                GetOptionalString(payload.RootElement, TokenNames.RefreshToken) ?? currentRefreshToken;
            var expiresAt = ResolveExpiry(
                GetOptionalInt(payload.RootElement, "expires_in"), refreshedAccessToken);

            var tokens = properties.GetTokens().ToDictionary(token => token.Name, token => token.Value);
            tokens[TokenNames.AccessToken] = refreshedAccessToken;
            tokens[TokenNames.RefreshToken] = refreshedRefreshToken;
            tokens[TokenNames.ExpiresAt] = expiresAt;

            properties.StoreTokens(tokens.Select(token => new AuthenticationToken
            {
                Name = token.Key,
                Value = token.Value
            }));

            return true;
        }
    }

    /// <summary>
    /// Whether the stored access token is close enough to expiry to be replaced.
    /// </summary>
    /// <remarks>
    /// An expiry that is missing or unreadable used to answer "no". That is the wrong way
    /// round: not knowing when a token expires is not evidence that it has not, so the app
    /// went on presenting a token that may well have been dead and every call to the API
    /// came back 401 with nothing saying why. Refreshing instead costs one round trip and
    /// settles the question, and the refresh writes a readable expiry back, so an
    /// unreadable one does not persist and cannot turn into a refresh on every request.
    /// </remarks>
    internal static bool ShouldRefresh(string? expiresAtRaw)
    {
        if (string.IsNullOrWhiteSpace(expiresAtRaw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            return true;
        }

        return expiresAt <= DateTimeOffset.UtcNow.Add(RefreshSkew);
    }

    /// <summary>
    /// When the new access token expires, as a round-trippable string.
    /// </summary>
    /// <remarks>
    /// <c>expires_in</c> is only RECOMMENDED by RFC 6749, so when the token response omits
    /// it the access token's own <c>exp</c> claim is read instead. If neither can be had,
    /// a short floor is used rather than the "now" this used to fall back to: paired with
    /// <see cref="ShouldRefresh"/> treating an unknown expiry as due, storing a value that
    /// is already in the past would refresh on every single request.
    /// </remarks>
    internal static string ResolveExpiry(int? expiresIn, string accessToken)
    {
        if (expiresIn is { } seconds)
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("o", CultureInfo.InvariantCulture);
        }

        if (TryReadExpiry(accessToken) is { } fromToken)
        {
            return fromToken.ToString("o", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.UtcNow.Add(UnknownExpiryFloor).ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? TryReadExpiry(string accessToken)
    {
        try
        {
            if (!JwtTokenHandler.CanReadToken(accessToken))
                return null;

            var expiry = JwtTokenHandler.ReadJwtToken(accessToken).ValidTo;

            return expiry == DateTime.MinValue ? null : new DateTimeOffset(expiry, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static FormUrlEncodedContent CreateRefreshContent(OpenIdConnectOptions options, string refreshToken)
    {
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId!,
            [TokenNames.RefreshToken] = refreshToken
        };

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            values["client_secret"] = options.ClientSecret;
        }

        return new FormUrlEncodedContent(values);
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }

    private static int? GetOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return null;
    }

    private static string? GetRequiredString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }

    private static bool HasExpectedIssuer(string accessToken, string? expectedIssuer)
    {
        if (string.IsNullOrWhiteSpace(expectedIssuer))
            return true;

        try
        {
            if (!JwtTokenHandler.CanReadToken(accessToken))
                return false;

            var jwt = JwtTokenHandler.ReadJwtToken(accessToken);

            if (string.IsNullOrWhiteSpace(jwt.Issuer))
                return false;

            return string.Equals(
                NormalizeIssuer(jwt.Issuer),
                NormalizeIssuer(expectedIssuer),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeIssuer(string issuer)
    {
        return issuer.Trim().TrimEnd('/');
    }
}

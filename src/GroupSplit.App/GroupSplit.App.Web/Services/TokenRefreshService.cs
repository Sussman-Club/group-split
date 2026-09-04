using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using GroupSplit.App.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Services;

internal sealed class TokenRefreshService(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);

    /// <summary>How long a token is assumed good for when nothing says otherwise.</summary>
    private static readonly TimeSpan UnknownExpiryFloor = TimeSpan.FromMinutes(2);
    private static readonly JwtSecurityTokenHandler JwtTokenHandler = new();

    public async Task<string?> GetValidAccessTokenAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.User.Identity?.IsAuthenticated is not true)
        {
            return null;
        }

        var authResult = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!authResult.Succeeded || authResult.Principal is null || authResult.Properties is null)
        {
            return null;
        }

        var currentAccessToken = authResult.Properties.GetTokenValue(TokenNames.AccessToken);
        if (string.IsNullOrWhiteSpace(currentAccessToken))
        {
            return null;
        }

        var options = openIdConnectOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (options.ConfigurationManager is null)
        {
            return null;
        }

        var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        var expectedIssuer = configuration.Issuer ?? options.Authority;

        if (!HasExpectedIssuer(currentAccessToken, expectedIssuer))
        {
            return null;
        }

        if (!ShouldRefresh(authResult.Properties.GetTokenValue(TokenNames.ExpiresAt)))
        {
            return currentAccessToken;
        }

        var currentRefreshToken = authResult.Properties.GetTokenValue(TokenNames.RefreshToken);
        if (string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            return null;
        }

        var tokenEndpoint = configuration.TokenEndpoint;
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Content = CreateRefreshContent(options, currentRefreshToken);

        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var refreshedAccessToken = GetRequiredString(payload.RootElement, TokenNames.AccessToken);
        if (string.IsNullOrWhiteSpace(refreshedAccessToken))
        {
            return null;
        }

        var refreshedRefreshToken =
            GetOptionalString(payload.RootElement, TokenNames.RefreshToken) ?? currentRefreshToken;
        var expiresAt = ResolveExpiry(
            GetOptionalInt(payload.RootElement, "expires_in"), refreshedAccessToken);

        var tokens = authResult.Properties.GetTokens().ToDictionary(token => token.Name, token => token.Value);
        tokens[TokenNames.AccessToken] = refreshedAccessToken;
        tokens[TokenNames.RefreshToken] = refreshedRefreshToken;
        tokens[TokenNames.ExpiresAt] = expiresAt;

        authResult.Properties.StoreTokens(tokens.Select(token => new AuthenticationToken
        {
            Name = token.Key,
            Value = token.Value
        }));

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authResult.Principal,
            authResult.Properties);

        return refreshedAccessToken;
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
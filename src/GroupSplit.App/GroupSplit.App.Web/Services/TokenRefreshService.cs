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
        var expiresIn = GetOptionalInt(payload.RootElement, "expires_in");
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn ?? 0).ToString("o", CultureInfo.InvariantCulture);

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

    private static bool ShouldRefresh(string? expiresAtRaw)
    {
        if (string.IsNullOrWhiteSpace(expiresAtRaw))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            return false;
        }

        return expiresAt <= DateTimeOffset.UtcNow.Add(RefreshSkew);
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

    private bool HasExpectedIssuer(string accessToken, string? expectedIssuer)
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
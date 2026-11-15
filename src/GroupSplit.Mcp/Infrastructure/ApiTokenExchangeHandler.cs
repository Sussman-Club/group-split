using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Exchanges the current request's user token for each outgoing API request.</summary>
public sealed class ApiTokenExchangeHandler(IHttpClientFactory clients, IHttpContextAccessor accessor, IConfiguration config) : DelegatingHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = accessor.HttpContext ?? throw new InvalidOperationException("No HTTP request.");
        if (context.User.Identity?.IsAuthenticated != true ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            throw new UnauthorizedAccessException("An authenticated bearer token is required.");
        }

        // Keycloak authenticates this confidential client and verifies that the incoming
        // token was issued to it as an audience. Never send that token to the API.
        using var exchange = new HttpRequestMessage(HttpMethod.Post,
            config["Mcp:Issuer"]!.TrimEnd('/') + "/protocol/openid-connect/token");
        exchange.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["subject_token"] = authorization.Parameter,
            ["audience"] = "api",
            ["client_id"] = config["Mcp:ExchangeClientId"]!,
            ["client_secret"] = config["Mcp:ExchangeClientSecret"]!
        });
        using var tokenResponse = await clients.CreateClient("exchange").SendAsync(exchange, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Keycloak token exchange failed ({(int)tokenResponse.StatusCode}).");
        }
        var token = await tokenResponse.Content.ReadFromJsonAsync<ExchangeResponse>(Json, ct);
        if (string.IsNullOrWhiteSpace(token?.AccessToken) || !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Keycloak returned an invalid exchange response.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return await base.SendAsync(request, ct);
    }

    private sealed record ExchangeResponse([property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType);
}

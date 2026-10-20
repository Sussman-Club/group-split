using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.App.Services;

public interface IMobileBrowser
{
    Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callback, CancellationToken ct);
}

public interface IMobileTokenStore
{
    Task<string?> ReadAsync();
    Task WriteAsync(string value);
    void Clear();
}

/// <summary>One serialized token session shared by every API handler in the app.</summary>
public sealed class MobileSession(MobileOptions options, HttpClient http, IMobileBrowser browser,
    IMobileTokenStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Tokens? tokens;
    private bool loaded;
    private bool persistent;

    public async Task LoginAsync(bool remember, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var verifier = RandomValue();
            var state = RandomValue();
            var challenge = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var url = Query(options.Endpoint("auth"), new()
            {
                ["client_id"] = MobileOptions.ClientId, ["redirect_uri"] = MobileOptions.Callback.AbsoluteUri,
                ["response_type"] = "code", ["scope"] = "openid profile email",
                ["code_challenge_method"] = "S256", ["code_challenge"] = challenge, ["state"] = state
            });
            var result = await browser.AuthenticateAsync(url, MobileOptions.Callback, ct);
            if (!result.TryGetValue("state", out var returnedState) || returnedState != state
                || result.ContainsKey("error") || !result.TryGetValue("code", out var code)
                || string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Sign-in could not be verified. Please try again.");

            var response = await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = MobileOptions.Callback.AbsoluteUri, ["code_verifier"] = verifier
            }, ct);
            persistent = remember;
            await SaveAsync(response);
        }
        finally { gate.Release(); }
    }

    public async Task<string?> AccessTokenAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!loaded)
            {
                var saved = await store.ReadAsync();
                try { tokens = saved is null ? null : JsonSerializer.Deserialize(saved, MobileJsonContext.Default.Tokens); }
                catch (JsonException) { store.Clear(); }
                persistent = tokens is not null;
                loaded = true;
            }
            if (tokens is null) return null;
            if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return tokens.AccessToken;
            if (string.IsNullOrWhiteSpace(tokens.RefreshToken)) { Clear(); return null; }

            using var response = await http.PostAsync(options.Endpoint("token"), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = MobileOptions.ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken
            }), ct);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await response.Content.ReadFromJsonAsync(MobileJsonContext.Default.OAuthError, ct);
                if (error?.Error == "invalid_grant") { Clear(); return null; }
            }
            response.EnsureSuccessStatusCode();
            var refreshed = await ReadTokensAsync(response, ct);
            // A provider may leave the refresh token unchanged rather than return it again.
            await SaveAsync(refreshed with { RefreshToken = refreshed.RefreshToken ?? tokens.RefreshToken,
                IdToken = refreshed.IdToken ?? tokens.IdToken });
            return tokens!.AccessToken;
        }
        finally { gate.Release(); }
    }

    public async Task LogoutAsync(bool deleted = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Tokens? saved = tokens;
            if (!loaded)
            {
                try { saved = JsonSerializer.Deserialize(await store.ReadAsync() ?? "null", MobileJsonContext.Default.Tokens); }
                catch (JsonException) { /* A corrupt saved session must not prevent local sign-out. */ }
            }
            Clear(); // Local sign-out succeeds even when the authority cannot be reached.
            if (deleted || saved?.IdToken is null) return;
            var state = RandomValue();
            var result = await browser.AuthenticateAsync(Query(options.Endpoint("logout"), new()
            {
                ["client_id"] = MobileOptions.ClientId, ["id_token_hint"] = saved.IdToken,
                ["post_logout_redirect_uri"] = MobileOptions.Callback.AbsoluteUri, ["state"] = state
            }), MobileOptions.Callback, ct);
            if (!result.TryGetValue("state", out var returned) || returned != state)
                throw new InvalidOperationException("The app signed out, but authority sign-out could not be verified.");
        }
        finally { gate.Release(); }
    }

    public async Task InvalidateAsync(string rejectedToken, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            // A delayed 401 for an old request must not erase a newer sign-in or refresh.
            if (tokens?.AccessToken == rejectedToken) Clear();
        }
        finally { gate.Release(); }
    }

    private void Clear() { tokens = null; loaded = true; persistent = false; store.Clear(); }

    private async Task SaveAsync(Tokens value)
    {
        // Persist rotated credentials before making them available to another request.
        if (persistent) await store.WriteAsync(JsonSerializer.Serialize(value, MobileJsonContext.Default.Tokens));
        else store.Clear();
        tokens = value;
        loaded = true;
    }

    private async Task<Tokens> ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = MobileOptions.ClientId;
        using var response = await http.PostAsync(options.Endpoint("token"), new FormUrlEncodedContent(fields), ct);
        response.EnsureSuccessStatusCode();
        return await ReadTokensAsync(response, ct);
    }

    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync(MobileJsonContext.Default.TokenResponse, ct);
        if (value is null || string.IsNullOrWhiteSpace(value.AccessToken) || value.ExpiresIn <= 0
            || !string.Equals(value.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sign-in server returned an unusable token. Please try again.");
        return new(value.AccessToken, value.RefreshToken, value.IdToken, DateTimeOffset.UtcNow.AddSeconds(value.ExpiresIn));
    }

    private static string RandomValue() => Encode(RandomNumberGenerator.GetBytes(32));
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Uri Query(Uri endpoint, Dictionary<string, string> fields) => new(endpoint + "?" +
        string.Join("&", fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));

    public sealed record Tokens(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAt);
    internal sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
    internal sealed record OAuthError([property: JsonPropertyName("error")] string Error);
}

[JsonSerializable(typeof(MobileSession.Tokens))]
[JsonSerializable(typeof(MobileSession.TokenResponse))]
[JsonSerializable(typeof(MobileSession.OAuthError))]
internal partial class MobileJsonContext : JsonSerializerContext;

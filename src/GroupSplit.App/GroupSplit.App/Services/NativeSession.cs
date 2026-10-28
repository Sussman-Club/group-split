using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupSplit.App.Services;

/// <summary>One serialized token session shared by every API handler in the app.</summary>
public sealed class NativeSession(NativeOptions options, IHttpClientFactory clients, INativeBrowser browser,
    INativeTokenStore store) : IDisposable
{
    public const string IdentityClientName = "NativeIdentity";
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
                ["client_id"] = NativeOptions.ClientId, ["redirect_uri"] = NativeOptions.Callback.AbsoluteUri,
                ["response_type"] = "code", ["scope"] = "openid profile email",
                ["ui_locales"] = GroupSplit.Shared.AppLanguages.Resolve(System.Globalization.CultureInfo.CurrentUICulture.Name),
                ["code_challenge_method"] = "S256", ["code_challenge"] = challenge, ["state"] = state
            });
            var result = await browser.AuthenticateAsync(url, NativeOptions.Callback, ct);
            if (!result.TryGetValue("state", out var returnedState) || returnedState != state
                || result.ContainsKey("error") || !result.TryGetValue("code", out var code)
                || string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Sign-in could not be verified. Please try again.");

            var response = await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = NativeOptions.Callback.AbsoluteUri, ["code_verifier"] = verifier
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
                try { tokens = saved is null ? null : JsonSerializer.Deserialize(saved, NativeJsonContext.Default.Tokens); }
                catch (JsonException) { store.Clear(); }
                persistent = tokens is not null;
                loaded = true;
            }
            if (tokens is null) return null;
            if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return tokens.AccessToken;
            if (string.IsNullOrWhiteSpace(tokens.RefreshToken)) { Clear(); return null; }

            using var http = clients.CreateClient(IdentityClientName);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = NativeOptions.ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken
            });
            using var response = await http.PostAsync(options.Endpoint("token"), content, ct);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await response.Content.ReadFromJsonAsync(NativeJsonContext.Default.OAuthError, ct);
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
                try { saved = JsonSerializer.Deserialize(await store.ReadAsync() ?? "null", NativeJsonContext.Default.Tokens); }
                catch (JsonException) { /* A corrupt saved session must not prevent local sign-out. */ }
            }
            Clear(); // Local sign-out succeeds even when the authority cannot be reached.
            if (deleted || saved?.IdToken is null) return;
            var state = RandomValue();
            var result = await browser.AuthenticateAsync(Query(options.Endpoint("logout"), new()
            {
                ["client_id"] = NativeOptions.ClientId, ["id_token_hint"] = saved.IdToken,
                ["post_logout_redirect_uri"] = NativeOptions.Callback.AbsoluteUri, ["state"] = state
            }), NativeOptions.Callback, ct);
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
        if (persistent) await store.WriteAsync(JsonSerializer.Serialize(value, NativeJsonContext.Default.Tokens));
        else store.Clear();
        tokens = value;
        loaded = true;
    }

    private async Task<Tokens> ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = NativeOptions.ClientId;
        using var http = clients.CreateClient(IdentityClientName);
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(options.Endpoint("token"), content, ct);
        response.EnsureSuccessStatusCode();
        return await ReadTokensAsync(response, ct);
    }

    private static async Task<Tokens> ReadTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync(NativeJsonContext.Default.TokenResponse, ct);
        if (value is null || string.IsNullOrWhiteSpace(value.AccessToken) || value.ExpiresIn <= 0
            || !string.Equals(value.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sign-in server returned an unusable token. Please try again.");
        return new(value.AccessToken, value.RefreshToken, value.IdToken, DateTimeOffset.UtcNow.AddSeconds(value.ExpiresIn));
    }

    private static string RandomValue() => Encode(RandomNumberGenerator.GetBytes(32));
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Uri Query(Uri endpoint, Dictionary<string, string> fields) => new(endpoint + "?" +
        string.Join("&", fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));

    public void Dispose() => gate.Dispose();

    public sealed record Tokens(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAt);
    internal sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
    internal sealed record OAuthError([property: JsonPropertyName("error")] string Error);
}

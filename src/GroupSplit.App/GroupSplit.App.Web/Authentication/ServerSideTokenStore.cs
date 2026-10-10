using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace GroupSplit.App.Web.Authentication;

/// <summary>
/// Keeps a signed-in person's OIDC tokens on the server, beside their ticket rather than
/// inside it.
/// </summary>
/// <remarks>
/// The library's own store keeps them in the ticket, reached through <c>HttpContext</c> and
/// rewritten with <c>SignInAsync</c>. Neither works in the render mode this app deploys:
/// inside an interactive circuit the ambient <c>HttpContext</c> is the long-lived one the
/// circuit was opened over, its ticket was validated once when that connection was made,
/// and its response started with the WebSocket handshake -- so the tokens were frozen at
/// page load and could not be written back. Keyed by principal instead, this is readable
/// and writable from a request, a static render, or a circuit hours old.
/// <para>
/// Encrypted with the web app's Data Protection ring before it is written. The cache is
/// Redis, which snapshots to its volume every thirty seconds, and what is kept here is a
/// refresh token: anybody who could read the volume could otherwise act as everybody signed
/// in. An entry the ring cannot open -- written in plain text before this, or under a key
/// since lost -- is treated as absent, which signs that session out rather than failing it.
/// </para>
/// </remarks>
internal sealed class ServerSideTokenStore(
    IDistributedCache cache,
    IDataProtectionProvider dataProtection,
    ILogger<ServerSideTokenStore> logger) : IUserTokenStore
{
    /// <summary>
    /// The purpose the payloads are protected under. Part of key derivation, so changing it
    /// makes every stored entry unreadable -- which is to say, signs everybody out.
    /// </summary>
    internal const string ProtectorPurpose = "GroupSplit.App.Web.SessionTokens.v1";

    private readonly IDataProtector _protector = dataProtection.CreateProtector(ProtectorPurpose);

    /// <summary>
    /// As long as the longest session a cookie can name, so tokens never end one.
    /// </summary>
    /// <remarks>
    /// Sliding, and taken from the remembered lifetime rather than the ordinary one: a
    /// remembered session is thirty days and its ticket is not stored here. Set shorter,
    /// the failure is worse than being signed out -- the cookie and the ticket are both
    /// still good, so the app believes the person is signed in while every call it makes
    /// on their behalf has no token to make it with.
    /// </remarks>
    private static readonly TimeSpan Lifetime = AuthenticationExtensions.RememberedSessionLifetime;

    public async Task<TokenResult<TokenForParameters>> GetTokenAsync(
        ClaimsPrincipal user,
        UserTokenRequestParameters? parameters = null,
        CancellationToken ct = default)
    {
        if (SessionTokenKey.For(user) is not { } key)
        {
            return TokenResult.Failure("no_session", "The ticket carries no sign-in session identifier.");
        }

        var protectedPayload = await cache.GetAsync(key, ct);

        if (protectedPayload is not { Length: > 0 })
        {
            return TokenResult.Failure("no_stored_token", "No tokens are stored for this session.");
        }

        byte[] payload;

        try
        {
            payload = _protector.Unprotect(protectedPayload);
        }
        catch (CryptographicException)
        {
            // Not an error: an entry from before the payloads were protected, or one whose
            // key is gone. Either way there are no tokens to be had, the same as if nothing
            // were stored, and the person signs in again.
            logger.LogInformation("Stored tokens could not be unprotected and are being discarded.");

            await cache.RemoveAsync(key, ct);

            return TokenResult.Failure("no_stored_token", "No readable tokens are stored for this session.");
        }

        StoredTokens stored;

        try
        {
            stored = JsonSerializer.Deserialize<StoredTokens>(payload)
                     ?? throw new JsonException("null");
        }
        catch (JsonException e)
        {
            logger.LogWarning(e, "Stored tokens could not be read back and are being discarded.");

            await cache.RemoveAsync(key, ct);

            return TokenResult.Failure("unreadable_stored_token", "The stored tokens could not be read.");
        }

        var token = Read(stored);

        // Handed over alongside as well as inside: it is what the manager refreshes with,
        // and all it has to go on once the access token is gone.
        return new TokenForParameters(
            token,
            token.RefreshToken is { } refreshToken
                ? new UserRefreshToken(refreshToken, token.DPoPJsonWebKey)
                : null);
    }

    public async Task StoreTokenAsync(
        ClaimsPrincipal user,
        UserToken token,
        UserTokenRequestParameters? parameters = null,
        CancellationToken ct = default)
    {
        if (SessionTokenKey.For(user) is not { } key)
        {
            // Warned rather than thrown: a refresh that cannot be written down still hands
            // this request a working token, which beats an exception mid-render.
            logger.LogWarning(
                "Tokens were refreshed for a principal with no sign-in session identifier, so they "
                + "cannot be stored and the refresh will be repeated.");

            return;
        }

        await cache.SetAsync(
            key,
            _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(Store(token))),
            new DistributedCacheEntryOptions { SlidingExpiration = Lifetime },
            ct);
    }

    public async Task ClearTokenAsync(
        ClaimsPrincipal user,
        UserTokenRequestParameters? parameters = null,
        CancellationToken ct = default)
    {
        if (SessionTokenKey.For(user) is { } key)
        {
            await cache.RemoveAsync(key, ct);
        }
    }

    /// <summary>
    /// What is written down, in a shape this app owns rather than the library's. Mapped by
    /// the methods below, because inside the record every property name shadows the type of
    /// the same name.
    /// </summary>
    private sealed record StoredTokens(
        string AccessToken,
        string? AccessTokenType,
        string ClientId,
        DateTimeOffset Expiration,
        string? RefreshToken,
        string? IdentityToken,
        string? Scope);

    private static StoredTokens Store(UserToken token) => new(
        token.AccessToken.ToString(),
        token.AccessTokenType?.ToString(),
        token.ClientId.ToString(),
        token.Expiration,
        token.RefreshToken?.ToString(),
        token.IdentityToken?.ToString(),
        token.Scope?.ToString());

    private static UserToken Read(StoredTokens stored) => new()
    {
        AccessToken = AccessToken.Parse(stored.AccessToken),
        AccessTokenType = stored.AccessTokenType is null
            ? null
            : AccessTokenType.Parse(stored.AccessTokenType),
        ClientId = ClientId.Parse(stored.ClientId),
        Expiration = stored.Expiration,
        RefreshToken = stored.RefreshToken is null
            ? null
            : RefreshToken.Parse(stored.RefreshToken),
        IdentityToken = stored.IdentityToken is null
            ? null
            : IdentityToken.Parse(stored.IdentityToken),
        Scope = stored.Scope is null ? null : Scope.Parse(stored.Scope)
    };
}

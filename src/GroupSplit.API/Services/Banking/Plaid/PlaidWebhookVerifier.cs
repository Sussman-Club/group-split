using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Going.Plaid;
using Going.Plaid.Entity;
using Going.Plaid.WebhookVerificationKey;

namespace GroupSplit.API.Services.Banking.Plaid;

/// <summary>
/// Decides whether a webhook really came from Plaid.
/// </summary>
/// <remarks>
/// Plaid signs each webhook with an ES256 JWT in <c>Plaid-Verification</c> whose payload
/// carries a SHA-256 of the body. Verifying it is the whole of the authentication on the
/// one anonymous route in the API, so it is written to refuse rather than to cope: any step
/// that does not hold means false, and false means the request is dropped without being
/// parsed.
/// <para>
/// Four things are checked, and all four matter. The algorithm must be ES256, or a caller
/// could name one whose "signature" is easy to produce. The key must be one Plaid gives us
/// for that <c>kid</c>. The <c>iat</c> must be recent, or a captured webhook could be
/// replayed forever. And the body's hash must match, or a valid signature could be moved
/// onto a different body.
/// </para>
/// </remarks>
public sealed class PlaidWebhookVerifier(
    PlaidClient plaid,
    TimeProvider clock,
    ILogger<PlaidWebhookVerifier> logger)
{
    public const string HeaderName = "Plaid-Verification";

    /// <summary>
    /// How old a webhook may be. Plaid's own guidance; it is what stops a captured request
    /// from being replayed later.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Keys by <c>kid</c>. Plaid rotates them and asks that they be cached rather than
    /// fetched per webhook; an expired one is dropped and fetched again.
    /// </summary>
    private readonly ConcurrentDictionary<string, JWKPublicKey> _keys = new(StringComparer.Ordinal);

    public async Task<bool> VerifyAsync(IReadOnlyDictionary<string, string> headers, string body,
        CancellationToken ct = default)
    {
        if (!headers.TryGetValue(HeaderName, out var token) || string.IsNullOrWhiteSpace(token))
            return Refuse("no {Header} header", HeaderName);

        var parts = token.Split('.');

        if (parts.Length != 3)
            return Refuse("the token is not a three-part JWT");

        if (!TryReadJson(parts[0], out var header))
            return Refuse("the token header is not readable");

        using (header)
        {
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "ES256")
                return Refuse("the algorithm is not ES256");

            if (!header.RootElement.TryGetProperty("kid", out var kidValue)
                || kidValue.GetString() is not { Length: > 0 } kid)
            {
                return Refuse("the token names no key");
            }

            if (await KeyAsync(kid, ct) is not { } key)
                return Refuse("no key is available for kid {Kid}", kid);

            if (!Signed(parts, key))
                return Refuse("the signature does not verify");
        }

        if (!TryReadJson(parts[1], out var payload))
            return Refuse("the token payload is not readable");

        using (payload)
        {
            if (!payload.RootElement.TryGetProperty("iat", out var issuedAt)
                || !issuedAt.TryGetInt64(out var issuedAtSeconds))
            {
                return Refuse("the token has no issued-at");
            }

            var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);

            if (age > MaxAge || age < -MaxAge)
                return Refuse("the token was issued {Age} ago", age);

            if (!payload.RootElement.TryGetProperty("request_body_sha256", out var claimed)
                || claimed.GetString() is not { Length: > 0 } expected)
            {
                return Refuse("the token does not say what body it signed");
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

            // Fixed-time, because a comparison that returns early tells a caller how much of
            // a guess was right.
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
            {
                return Refuse("the body does not match what was signed");
            }
        }

        return true;
    }

    /// <summary>
    /// The key for a <c>kid</c>, from the cache or from Plaid. Null when Plaid does not
    /// know it, which is what an invented one looks like.
    /// </summary>
    private async Task<JWKPublicKey?> KeyAsync(string kid, CancellationToken ct)
    {
        if (_keys.TryGetValue(kid, out var cached) && !Expired(cached))
            return cached;

        var response = await plaid.WebhookVerificationKeyGetAsync(new WebhookVerificationKeyGetRequest
        {
            KeyId = kid
        });

        if (!response.IsSuccessStatusCode || response.Key is not { } key)
        {
            logger.LogWarning("Plaid returned no verification key for kid {Kid}: {Error}.",
                kid, response.Error?.ErrorCode);
            return null;
        }

        if (Expired(key))
            return null;

        _keys[kid] = key;

        return key;
    }

    private bool Expired(JWKPublicKey key) =>
        key.ExpiredAt is { } expiredAt && DateTimeOffset.FromUnixTimeSeconds(expiredAt) <= clock.GetUtcNow();

    /// <summary>
    /// Whether the signature over <c>header.payload</c> holds for this key.
    /// </summary>
    /// <remarks>
    /// JWS carries an ECDSA signature as r followed by s, fixed width, which is exactly the
    /// format <see cref="ECDsa.VerifyData(byte[], byte[], HashAlgorithmName)"/> expects. No
    /// DER unwrapping is needed, and doing any would be a bug.
    /// </remarks>
    private bool Signed(string[] parts, JWKPublicKey key)
    {
        try
        {
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = FromBase64Url(key.X ?? string.Empty),
                    Y = FromBase64Url(key.Y ?? string.Empty)
                }
            });

            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

            return ecdsa.VerifyData(signed, FromBase64Url(parts[2]), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            logger.LogWarning(e, "A Plaid webhook signature could not be checked.");
            return false;
        }
    }

    private static bool TryReadJson(string part, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(FromBase64Url(part));
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    /// <summary>
    /// Logs why and answers false. The reason is for us; the caller is told nothing but that
    /// it was refused.
    /// </summary>
    private bool Refuse(string reason, params object?[] args)
    {
        logger.LogWarning("A Plaid webhook was refused: " + reason + ".", args);
        return false;
    }
}

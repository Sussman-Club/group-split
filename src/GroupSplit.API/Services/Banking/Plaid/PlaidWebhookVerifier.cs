using System.Collections.Concurrent;
using System.Net;
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

    /// <summary>
    /// Key ids Plaid said it does not have, and when it said so.
    /// </summary>
    /// <remarks>
    /// Capped, and emptied wholesale when it fills. The shape check in front of this bounds
    /// how long a key id may be and not how many there are, so an anonymous caller inventing
    /// a well-shaped one per request would otherwise trade an outbound call at Plaid for a
    /// permanent entry here -- the same denial of service wearing a different hat. Emptying
    /// rather than evicting the oldest because the cost of being wrong is one extra lookup:
    /// this is a courtesy to Plaid, not a correctness mechanism.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unknown = new(StringComparer.Ordinal);

    /// <summary>How long a "Plaid does not have this key" answer is trusted.</summary>
    private static readonly TimeSpan UnknownKeyMemory = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many refusals are remembered at once. Far more than the handful of key ids Plaid
    /// has in rotation, and small enough that filling it costs nothing worth having.
    /// </summary>
    private const int MaxUnknownKeys = 1024;

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

            // Shape-checked before it is looked up. A kid is Plaid's own identifier and it
            // is short and alphanumeric; anything else cannot be one, and the check matters
            // because the lookup below is an outbound call to Plaid made on behalf of an
            // unauthenticated caller. Without it, a stranger inventing a fresh kid per
            // request turns this route into a one-for-one amplifier at our own provider.
            if (!PlausibleKeyId(kid))
                return Refuse("the token names a key that is not shaped like one");

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

        // Remembered for a while, the same way a hit is. Only successes were cached before,
        // so a kid Plaid does not know cost an outbound call every single time it was
        // offered -- and offering one is free to anybody who can reach this route.
        if (_unknown.TryGetValue(kid, out var refusedAt) && clock.GetUtcNow() - refusedAt < UnknownKeyMemory)
            return null;

        // No cancellation token: Going.Plaid's generated client does not take one here. The
        // outbound call is bounded by the HttpClient's own timeout instead.
        var response = await plaid.WebhookVerificationKeyGetAsync(new WebhookVerificationKeyGetRequest
        {
            KeyId = kid
        });

        if (!response.IsSuccessStatusCode || response.Key is not { } key)
        {
            logger.LogWarning("Plaid returned no verification key for kid {Kid}: {Error}.",
                kid, response.Error?.ErrorCode);

            // Only remembered when Plaid actually answered the question. A 429 or a bad
            // moment at their end says nothing about the key, and caching one as unknown
            // would refuse ten minutes of genuine webhooks signed with a key that is real
            // and current -- turning a blip at the provider into lost notifications here.
            if (Refused(response.StatusCode))
                Remember(kid);

            return null;
        }

        _unknown.TryRemove(kid, out _);

        if (Expired(key))
            return null;

        _keys[kid] = key;

        return key;
    }

    /// <summary>
    /// Whether Plaid answered the question rather than failing to answer it.
    /// </summary>
    /// <remarks>
    /// Named individually rather than taken as "any 4xx", because a 429 and a 5xx are Plaid
    /// declining to answer rather than answering -- caching either as "no such key" would
    /// refuse ten minutes of genuine webhooks signed with a key that is real and current.
    /// <para>
    /// It is not a clean split. Plaid answers an unknown key id with a 400
    /// (<c>INVALID_WEBHOOK_VERIFICATION_KEY_ID</c>) and answers bad credentials or the wrong
    /// environment with a 400 as well, so a misconfigured deployment does land here, and
    /// after somebody fixes it there is up to ten minutes of refusals per key. The status
    /// alone cannot tell those apart; <c>Error.ErrorCode</c> could, and if this ever costs
    /// anybody real time that is where to look.
    /// </para>
    /// </remarks>
    private static bool Refused(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;

    private void Remember(string kid)
    {
        // Emptied rather than grown. Nothing here is worth a bounded cache with an eviction
        // policy: the entries are a courtesy to Plaid's rate limit, and losing all of them
        // costs one lookup each.
        if (_unknown.Count >= MaxUnknownKeys)
            _unknown.Clear();

        _unknown[kid] = clock.GetUtcNow();
    }

    /// <summary>
    /// Whether a <c>kid</c> could be one of Plaid's at all. Deliberately a shape check and
    /// not a guess at their format: it exists to bound what an anonymous caller can make
    /// this service go and ask about, not to decide which keys are real. That is Plaid's
    /// answer, and it is cached either way.
    /// </summary>
    private static bool PlausibleKeyId(string kid) =>
        kid.Length <= 64 && kid.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

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

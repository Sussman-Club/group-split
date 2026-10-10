using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GroupSplit.API.Services.Banking.Plaid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GroupSplit.API.Test.Banking.Plaid;

/// <summary>
/// The whole of the authentication on the API's one anonymous route.
/// </summary>
/// <remarks>
/// Signed with a real P-256 key made here, and verified against the public half served the
/// way Plaid serves it. Every test but the first breaks exactly one of the four things that
/// have to hold, so a check quietly deleted from the verifier fails a test that names it.
/// </remarks>
public class PlaidWebhookVerifierTest
{
    private const string KeyPath = "/webhook_verification_key/get";
    private const string Kid = "6c5516e1-92dc-479e-a8ff-5a51992e0001";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly PlaidTestServer _plaid = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlaidWebhookVerifier Verifier() =>
        new(_plaid.Client(), _clock, NullLogger<PlaidWebhookVerifier>.Instance);

    [Fact]
    public async Task A_webhook_Plaid_really_signed_is_accepted()
    {
        const string body = """{"webhook_type":"TRANSACTIONS","webhook_code":"SYNC_UPDATES_AVAILABLE"}""";
        ServeKey();

        var accepted = await Verifier().VerifyAsync(Headers(Token(body)), body, Ct);

        Assert.True(accepted);
    }

    [Fact]
    public async Task The_key_is_fetched_once_and_then_remembered()
    {
        const string body = """{"a":1}""";
        const string other = """{"a":2}""";
        ServeKey();

        var verifier = Verifier();

        Assert.True(await verifier.VerifyAsync(Headers(Token(body)), body, Ct));
        Assert.True(await verifier.VerifyAsync(Headers(Token(other)), other, Ct));

        // One fetch for two webhooks. Plaid asks that these be cached, and a second recorded
        // answer was never queued, so a second fetch would have thrown.
        Assert.Single(_plaid.Requests);
    }

    [Fact]
    public async Task A_signature_moved_onto_a_different_body_is_refused()
    {
        ServeKey();
        var token = Token("""{"amount":1}""");

        var accepted = await Verifier().VerifyAsync(Headers(token), """{"amount":1000000}""", Ct);

        Assert.False(accepted);
    }

    [Fact]
    public async Task A_captured_webhook_replayed_later_is_refused()
    {
        const string body = """{"a":1}""";
        ServeKey();
        var token = Token(body);

        _clock.Advance(TimeSpan.FromMinutes(6));

        var accepted = await Verifier().VerifyAsync(Headers(token), body, Ct);

        Assert.False(accepted);
    }

    /// <summary>
    /// The age check alone leaves five minutes in which a captured webhook can be sent again
    /// as often as anybody likes.
    /// </summary>
    [Fact]
    public async Task The_same_webhook_sent_twice_inside_the_window_is_refused_the_second_time()
    {
        const string body = """{"webhook_type":"TRANSACTIONS","webhook_code":"SYNC_UPDATES_AVAILABLE"}""";
        ServeKey();
        var verifier = Verifier();
        var token = Token(body);

        Assert.True(await verifier.VerifyAsync(Headers(token), body, Ct));

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(await verifier.VerifyAsync(Headers(token), body, Ct));
    }

    /// <summary>
    /// What is remembered is the pair. The same body signed again later is a new webhook --
    /// Plaid sends identical notifications for the same item routinely.
    /// </summary>
    [Fact]
    public async Task The_same_body_signed_again_later_is_a_new_webhook()
    {
        const string body = """{"webhook_type":"TRANSACTIONS","webhook_code":"SYNC_UPDATES_AVAILABLE"}""";
        ServeKey();
        var verifier = Verifier();

        Assert.True(await verifier.VerifyAsync(Headers(Token(body)), body, Ct));

        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(await verifier.VerifyAsync(Headers(Token(body)), body, Ct));
    }

    /// <summary>
    /// A forgery is refused before it is remembered, so it cannot use up the slot the genuine
    /// webhook with the same body and second would need.
    /// </summary>
    [Fact]
    public async Task A_refused_webhook_is_not_remembered()
    {
        const string body = """{"a":1}""";
        ServeKey();
        var verifier = Verifier();
        var genuine = Token(body);
        var parts = genuine.Split('.');
        var forged = $"{parts[0]}.{parts[1]}.{Convert.ToBase64String(new byte[64]).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

        Assert.False(await verifier.VerifyAsync(Headers(forged), body, Ct));
        Assert.True(await verifier.VerifyAsync(Headers(genuine), body, Ct));
    }

    [Fact]
    public async Task A_token_signed_with_a_key_Plaid_does_not_know_is_refused()
    {
        const string body = """{"a":1}""";
        _plaid.Answer(KeyPath, """{"error_type":"INVALID_INPUT","error_code":"INVALID_API_KEYS","error_message":"no such key","request_id":"r"}""",
            System.Net.HttpStatusCode.BadRequest);

        var accepted = await Verifier().VerifyAsync(Headers(Token(body)), body, Ct);

        Assert.False(accepted);
    }

    [Fact]
    public async Task A_token_naming_a_weaker_algorithm_is_refused_before_any_key_is_fetched()
    {
        const string body = """{"a":1}""";

        // "none" is the classic one: a token with no signature at all, which some libraries
        // will happily accept if they take the header's word for the algorithm.
        var token = Unsigned(Header("none", Kid), Payload(body, _clock.GetUtcNow()));

        var accepted = await Verifier().VerifyAsync(Headers(token), body, Ct);

        Assert.False(accepted);
        // Nothing was fetched, because the token was refused before it was worth asking.
        Assert.Empty(_plaid.Requests);
    }

    [Fact]
    public async Task An_expired_key_is_not_used()
    {
        const string body = """{"a":1}""";
        ServeKey(expiredAt: _clock.GetUtcNow().AddMinutes(-1));

        var accepted = await Verifier().VerifyAsync(Headers(Token(body)), body, Ct);

        Assert.False(accepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    public async Task A_token_that_is_not_a_token_is_refused(string token)
    {
        var accepted = await Verifier().VerifyAsync(Headers(token), "{}", Ct);

        Assert.False(accepted);
    }

    [Fact]
    public async Task A_request_with_no_verification_header_is_refused()
    {
        var accepted = await Verifier().VerifyAsync(new Dictionary<string, string>(), "{}", Ct);

        Assert.False(accepted);
    }

    // ---- signing, the way Plaid does it ----------------------------------------------------

    private static Dictionary<string, string> Headers(string token) =>
        new(StringComparer.OrdinalIgnoreCase) { [PlaidWebhookVerifier.HeaderName] = token };

    /// <summary>A genuine ES256 token over this body, issued now.</summary>
    private string Token(string body)
    {
        var signingInput = $"{Header("ES256", Kid)}.{Payload(body, _clock.GetUtcNow())}";
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Unsigned(string header, string payload) => $"{header}.{payload}.";

    private static string Header(string algorithm, string kid) =>
        Base64Url(Encoding.UTF8.GetBytes($$"""{"alg":"{{algorithm}}","kid":"{{kid}}","typ":"JWT"}"""));

    private static string Payload(string body, DateTimeOffset issuedAt)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        return Base64Url(Encoding.UTF8.GetBytes(
            $$"""{"iat":{{issuedAt.ToUnixTimeSeconds()}},"request_body_sha256":"{{hash}}"}"""));
    }

    /// <summary>The public half of the signing key, in the shape Plaid serves it.</summary>
    private void ServeKey(DateTimeOffset? expiredAt = null)
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        var key = new
        {
            key = new
            {
                alg = "ES256",
                crv = "P-256",
                kid = Kid,
                kty = "EC",
                use = "sig",
                x = Base64Url(parameters.Q.X!),
                y = Base64Url(parameters.Q.Y!),
                created_at = _clock.GetUtcNow().AddDays(-1).ToUnixTimeSeconds(),
                expired_at = expiredAt?.ToUnixTimeSeconds()
            },
            request_id = "req-key"
        };

        _plaid.Answer(KeyPath, JsonSerializer.Serialize(key));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

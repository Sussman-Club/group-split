using System.Text;
using System.Text.Json;
using GroupSplit.Cli.Auth;

namespace GroupSplit.Cli.Test;

public sealed class JwtClaimsTests
{
    [Fact]
    public void Reads_the_username_subject_and_expiry_from_the_payload()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var (username, subject, expiresAt) = JwtClaims.Read(Token(new
        {
            preferred_username = "anabel",
            sub = "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            exp = expires
        }));

        Assert.Equal("anabel", username);
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", subject);
        Assert.Equal(expires, expiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void Falls_back_to_email_then_name_when_there_is_no_preferred_username()
    {
        Assert.Equal("a@example.com", JwtClaims.Read(Token(new { email = "a@example.com" })).Username);
        Assert.Equal("Anabel", JwtClaims.Read(Token(new { name = "Anabel" })).Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!not-base64!!!.c")]
    public void Malformed_tokens_yield_nothing_rather_than_throwing(string token)
    {
        // This only ever labels a status line, so a token it cannot read must not be able
        // to take down the command that was really being run.
        var (username, subject, expiresAt) = JwtClaims.Read(token);

        Assert.Null(username);
        Assert.Null(subject);
        Assert.Null(expiresAt);
    }

    [Fact]
    public void Payloads_needing_base64url_padding_still_decode()
    {
        // Real Keycloak tokens are unpadded base64url; a decoder that assumes padding
        // works on some tokens and not others depending on payload length.
        var payload = JwtClaims.Read(Token(new { preferred_username = "abc" }));

        Assert.Equal("abc", payload.Username);
    }

    private static string Token(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"header.{encoded}.signature";
    }
}

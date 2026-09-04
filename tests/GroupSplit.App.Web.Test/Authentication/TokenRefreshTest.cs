using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using GroupSplit.App.Web.Services;
using Microsoft.IdentityModel.Tokens;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// The two decisions the silent-refresh path makes on its own: whether the stored access
/// token is due for replacement, and what expiry to write back after replacing it. Both
/// were untested, and the first of them was answering the wrong way round.
/// </summary>
public class TokenRefreshTest
{
    private static string Iso(DateTimeOffset moment) =>
        moment.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>An unsigned JWT is enough: nothing here validates a signature.</summary>
    private static string TokenExpiringAt(DateTimeOffset expiry)
    {
        var token = new JwtSecurityToken(
            issuer: "https://keycloak.test/realms/group-split",
            expires: expiry.UtcDateTime,
            notBefore: DateTime.UtcNow.AddMinutes(-5));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ---- ShouldRefresh --------------------------------------------------------------

    [Fact]
    public void A_token_with_plenty_of_life_left_is_kept()
    {
        Assert.False(
            TokenRefreshService.ShouldRefresh(Iso(DateTimeOffset.UtcNow.AddMinutes(30))));
    }

    [Fact]
    public void An_expired_token_is_refreshed()
    {
        Assert.True(
            TokenRefreshService.ShouldRefresh(Iso(DateTimeOffset.UtcNow.AddMinutes(-1))));
    }

    /// <summary>
    /// The skew is what stops a token expiring in flight, between the check here and the
    /// API reading it at the other end.
    /// </summary>
    [Fact]
    public void A_token_inside_the_skew_is_refreshed_before_it_expires()
    {
        Assert.True(
            TokenRefreshService.ShouldRefresh(Iso(DateTimeOffset.UtcNow.AddSeconds(30))));
    }

    [Fact]
    public void A_token_just_outside_the_skew_is_kept()
    {
        Assert.False(
            TokenRefreshService.ShouldRefresh(Iso(DateTimeOffset.UtcNow.AddMinutes(2))));
    }

    /// <summary>
    /// The defect. Not knowing when a token expires is not evidence that it has not, but
    /// this used to answer "keep it": the app went on presenting a token that may well
    /// have been dead, and every call to the API came back 401 with nothing saying why.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("1756944000")] // A unix timestamp, which is not the format this stores.
    public void An_expiry_that_cannot_be_read_is_refreshed_rather_than_trusted(string? stored)
    {
        Assert.True(TokenRefreshService.ShouldRefresh(stored));
    }

    /// <summary>
    /// An expiry written in another offset is still the same instant, and must not be read
    /// as local time — that would move it by hours in either direction.
    /// </summary>
    [Fact]
    public void An_expiry_stored_with_an_offset_is_read_as_the_instant_it_names()
    {
        var wellInTheFuture = new DateTimeOffset(
            DateTime.UtcNow.AddHours(5).Ticks, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(-8));

        Assert.False(TokenRefreshService.ShouldRefresh(Iso(wellInTheFuture)));
    }

    // ---- ResolveExpiry --------------------------------------------------------------

    [Fact]
    public void The_lifetime_the_token_response_gives_is_used()
    {
        var resolved = DateTimeOffset.Parse(
            TokenRefreshService.ResolveExpiry(300, TokenExpiringAt(DateTimeOffset.UtcNow.AddDays(1))),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.InRange(resolved,
            DateTimeOffset.UtcNow.AddSeconds(280), DateTimeOffset.UtcNow.AddSeconds(320));
    }

    /// <summary>
    /// expires_in is only RECOMMENDED by RFC 6749, so a response without one falls back to
    /// the token's own exp claim, which is authoritative.
    /// </summary>
    [Fact]
    public void Without_a_stated_lifetime_the_tokens_own_expiry_is_used()
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(17);

        var resolved = DateTimeOffset.Parse(
            TokenRefreshService.ResolveExpiry(null, TokenExpiringAt(expiry)),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        // JWT exp has one-second resolution.
        Assert.InRange(resolved, expiry.AddSeconds(-2), expiry.AddSeconds(2));
    }

    /// <summary>
    /// The case that would otherwise loop. The old code stored "now" when it had no
    /// lifetime, and with an unknown expiry now meaning "refresh", that would have asked
    /// for a new token on every single request.
    /// </summary>
    [Theory]
    [InlineData("not a jwt")]
    [InlineData("")]
    public void With_nothing_to_go_on_the_expiry_is_still_in_the_future(string accessToken)
    {
        var resolved = DateTimeOffset.Parse(
            TokenRefreshService.ResolveExpiry(null, accessToken),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.True(resolved > DateTimeOffset.UtcNow,
            "an expiry in the past would refresh again on the very next request");

        // And far enough out that the skew does not pull it straight back in.
        Assert.False(TokenRefreshService.ShouldRefresh(Iso(resolved)));
    }

    /// <summary>
    /// The round trip the service actually performs: what ResolveExpiry writes has to be
    /// something ShouldRefresh can read back.
    /// </summary>
    [Fact]
    public void What_is_written_after_a_refresh_can_be_read_back()
    {
        var written = TokenRefreshService.ResolveExpiry(
            3600, TokenExpiringAt(DateTimeOffset.UtcNow.AddHours(1)));

        Assert.False(TokenRefreshService.ShouldRefresh(written));
    }

    [Fact]
    public void A_refresh_that_returns_an_already_expired_lifetime_is_due_immediately()
    {
        var written = TokenRefreshService.ResolveExpiry(0, "not a jwt");

        Assert.True(TokenRefreshService.ShouldRefresh(written));
    }
}

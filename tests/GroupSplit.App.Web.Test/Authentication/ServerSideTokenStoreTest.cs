using System.Security.Claims;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using GroupSplit.App.Web.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// The store the tokens now live in, instead of the sign-in ticket.
/// </summary>
public class ServerSideTokenStoreTest
{
    [Fact]
    public async Task Tokens_survive_a_round_trip()
    {
        var store = Build();
        var user = SignedIn();

        await store.StoreTokenAsync(user, Token("an-access-token", "a-refresh-token"));

        var read = await store.GetTokenAsync(user);

        Assert.True(read.WasSuccessful(out var tokens));

        var token = Assert.IsType<UserToken>(tokens.TokenForSpecifiedParameters);

        Assert.Equal("an-access-token", token.AccessToken.ToString());
        Assert.Equal("a-refresh-token", token.RefreshToken?.ToString());
        Assert.Equal("an-id-token", token.IdentityToken?.ToString());
    }

    /// <summary>
    /// The manager refreshes with what comes back on the side of the result, not with what
    /// is inside the access token -- so a round trip that dropped it would leave a session
    /// unable to refresh at all, and it would look fine until the access token expired.
    /// </summary>
    [Fact]
    public async Task The_refresh_token_comes_back_where_the_manager_looks_for_it()
    {
        var store = Build();
        var user = SignedIn();

        await store.StoreTokenAsync(user, Token("an-access-token", "a-refresh-token"));

        var read = await store.GetTokenAsync(user);

        Assert.True(read.WasSuccessful(out var tokens));
        Assert.Equal("a-refresh-token", tokens.RefreshToken?.RefreshToken.ToString());
    }

    /// <summary>
    /// Why the session claim exists at all. Keyed on the person instead, two browsers signed
    /// in as the same person would share one entry -- and because Keycloak rotates refresh
    /// tokens one-time-use, each refresh in one would retire the token the other was holding
    /// and sign it out.
    /// </summary>
    [Fact]
    public async Task Two_sessions_of_the_same_person_do_not_share_tokens()
    {
        var store = Build();

        var oneBrowser = SignedIn();
        var anotherBrowser = SignedIn();

        await store.StoreTokenAsync(oneBrowser, Token("one-access-token", "one-refresh-token"));
        await store.StoreTokenAsync(anotherBrowser, Token("another-access-token", "another-refresh-token"));

        var read = await store.GetTokenAsync(oneBrowser);

        Assert.True(read.WasSuccessful(out var tokens));
        Assert.Equal("one-access-token", tokens.TokenForSpecifiedParameters?.AccessToken.ToString());
    }

    /// <summary>
    /// A refresh token left behind outlives the session it belonged to, so signing out has
    /// to reach it -- which is what the cookie handler's OnSigningOut is wired to.
    /// </summary>
    [Fact]
    public async Task Clearing_a_session_leaves_nothing_to_refresh_with()
    {
        var store = Build();
        var user = SignedIn();

        await store.StoreTokenAsync(user, Token("an-access-token", "a-refresh-token"));
        await store.ClearTokenAsync(user);

        Assert.False((await store.GetTokenAsync(user)).Succeeded);
    }

    [Fact]
    public async Task A_session_with_nothing_stored_fails_rather_than_returning_an_empty_token()
    {
        Assert.False((await Build().GetTokenAsync(SignedIn())).Succeeded);
    }

    /// <summary>
    /// A ticket minted before the session claim existed, which is every ticket in flight at
    /// the moment this deploys. Signing in again is the answer; guessing a key is not.
    /// </summary>
    [Fact]
    public async Task A_ticket_with_no_session_claim_fails_rather_than_guessing()
    {
        var store = Build();

        var withoutSessionClaim = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "a-subject")],
                CookieAuthenticationDefaults.AuthenticationScheme));

        Assert.False((await store.GetTokenAsync(withoutSessionClaim)).Succeeded);

        // And storing is refused rather than thrown, so a refresh that cannot be written
        // down still hands this request a working token.
        await store.StoreTokenAsync(withoutSessionClaim, Token("an-access-token", "a-refresh-token"));
    }

    private static ServerSideTokenStore Build() =>
        new(
            new MemoryDistributedCache(
                new OptionsWrapper<MemoryDistributedCacheOptions>(new MemoryDistributedCacheOptions())),
            NullLogger<ServerSideTokenStore>.Instance);

    /// <summary>A principal as it comes out of a sign-in, carrying its session claim.</summary>
    private static ClaimsPrincipal SignedIn()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "a-subject")],
            CookieAuthenticationDefaults.AuthenticationScheme);

        SessionTokenKey.Mint(identity);

        return new ClaimsPrincipal(identity);
    }

    private static UserToken Token(string accessToken, string refreshToken) => new()
    {
        AccessToken = AccessToken.Parse(accessToken),
        AccessTokenType = AccessTokenType.Parse("Bearer"),
        ClientId = ClientId.Parse("web-app"),
        Expiration = DateTimeOffset.UtcNow.AddMinutes(5),
        RefreshToken = RefreshToken.Parse(refreshToken),
        IdentityToken = IdentityToken.Parse("an-id-token"),
        Scope = Scope.Parse("openid email")
    };
}

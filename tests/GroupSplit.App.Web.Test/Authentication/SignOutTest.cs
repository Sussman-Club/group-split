using System.Security.Claims;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using GroupSplit.App.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// What signing out has to take with it.
/// </summary>
/// <remarks>
/// What it does <em>not</em> do is supply the <c>id_token_hint</c>. That was tried here and
/// is the wrong place: the handler builds the hint from
/// <c>HttpContext.GetTokenAsync(SignOutScheme, "id_token")</c>, which reads the ticket and
/// nowhere else, so a token put into the sign-out properties is never looked at. The id
/// token goes into the ticket at sign-in instead.
/// </remarks>
public class SignOutTest
{
    /// <summary>A refresh token left in the store outlives the session it belonged to.</summary>
    [Fact]
    public async Task The_tokens_are_thrown_away()
    {
        var (store, context, user) = await SigningOut();

        await AuthenticationExtensions.OnSigningOut(context);

        Assert.False((await store.GetTokenAsync(user)).Succeeded);
    }

    /// <summary>
    /// Signing out a session with nothing stored -- an idle one, or one already signed out
    /// in another tab -- still has to complete rather than throw.
    /// </summary>
    [Fact]
    public async Task A_session_with_nothing_stored_still_signs_out()
    {
        var (_, context, _) = await SigningOut(store: false);

        await AuthenticationExtensions.OnSigningOut(context);
    }

    private static async Task<(ServerSideTokenStore Store, CookieSigningOutContext Context, ClaimsPrincipal User)>
        SigningOut(bool store = true)
    {
        var tokenStore = new ServerSideTokenStore(
            new MemoryDistributedCache(
                new OptionsWrapper<MemoryDistributedCacheOptions>(new MemoryDistributedCacheOptions())),
            NullLogger<ServerSideTokenStore>.Instance);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "a-subject")],
            CookieAuthenticationDefaults.AuthenticationScheme);

        SessionTokenKey.Mint(identity);

        var user = new ClaimsPrincipal(identity);

        if (store)
        {
            await tokenStore.StoreTokenAsync(user, new UserToken
            {
                AccessToken = AccessToken.Parse("an-access-token"),
                AccessTokenType = AccessTokenType.Parse("Bearer"),
                ClientId = ClientId.Parse("web-app"),
                Expiration = DateTimeOffset.UtcNow.AddMinutes(5),
                RefreshToken = RefreshToken.Parse("a-refresh-token"),
                IdentityToken = IdentityToken.Parse("an-id-token")
            });
        }

        var services = new ServiceCollection();

        services.AddSingleton<IUserTokenStore>(tokenStore);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user
        };

        var context = new CookieSigningOutContext(
            httpContext,
            new AuthenticationScheme(
                CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationProperties(),
            new CookieOptions());

        return (tokenStore, context, user);
    }
}

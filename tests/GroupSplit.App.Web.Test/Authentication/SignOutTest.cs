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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GroupSplit.App.Web.Test.Authentication;

/// <summary>
/// Signing out has two jobs that pull against each other, and the order they happen in is
/// the whole test.
/// </summary>
public class SignOutTest
{
    /// <summary>
    /// A refresh token left in the store outlives the session it belonged to.
    /// </summary>
    [Fact]
    public async Task The_tokens_are_thrown_away()
    {
        var (store, context, user) = await SigningOut();

        await AuthenticationExtensions.OnSigningOut(context);

        Assert.False((await store.GetTokenAsync(user, ct: TestContext.Current.CancellationToken)).Succeeded);
    }

    /// <summary>
    /// The bug this test exists for. The hint used to be read by a second hook on the OIDC
    /// handler, which runs after the cookie has signed out -- by which point the store had
    /// already been emptied, so no hint was sent, and Keycloak answered the logout with a
    /// confirmation page instead of ending the session.
    /// <para>
    /// Written into the properties rather than onto the protocol message, which is where the
    /// handler looks by default and is what makes it independent of the order the schemes
    /// are signed out in.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_sign_out_is_given_the_id_token_to_identify_the_session_with()
    {
        var (_, context, _) = await SigningOut();

        await AuthenticationExtensions.OnSigningOut(context);

        Assert.Equal(
            "an-id-token",
            context.Properties.GetTokenValue(OpenIdConnectParameterNames.IdToken));
    }

    /// <summary>
    /// Signing out a session that has no tokens stored -- an idle one, or one already signed
    /// out in another tab -- still has to complete rather than throw.
    /// </summary>
    [Fact]
    public async Task A_session_with_nothing_stored_still_signs_out()
    {
        var (_, context, _) = await SigningOut(store: false);

        await AuthenticationExtensions.OnSigningOut(context);

        Assert.Null(context.Properties.GetTokenValue(OpenIdConnectParameterNames.IdToken));
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

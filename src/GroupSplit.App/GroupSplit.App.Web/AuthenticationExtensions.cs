using System.Security.Claims;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using GroupSplit.App.Web.Authentication;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

// RemoveAllResilienceHandlers is still experimental, and taken anyway: what it turns off
// is a retry that cannot safely be applied to a refresh-token exchange. See its use below.
#pragma warning disable EXTEXP0001

namespace GroupSplit.App.Web;

public static class AuthenticationExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddGroupSplitAuthentication()
        {
            // Service discovery supplies the authority in development. Elsewhere it is
            // configured, and it is the one setting that carries the scheme the browser
            // will use: Keycloak is published to the browser alongside the app, so the
            // two share an origin scheme in every topology we deploy.
            var authority = builder.Environment.IsDevelopment()
                ? null
                : builder.Configuration["Keycloak:Authority"]
                  ?? throw new InvalidOperationException(
                      "Keycloak:Authority must be configured outside of development.");

            // A browser discards a `Secure` cookie that did not arrive over TLS, so a
            // deployment served over plain HTTP cannot keep the defaults the sign-in
            // cookies ship with. Putting TLS in front of the app turns them all back on.
            var overPlainHttp = authority is not null
                && !authority.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

            // Holds the ticket server-side, leaving the cookie small enough not to blow
            // past Kestrel's header limit. The cache behind it is Redis, registered by the
            // host; this is TryAdd, so it only supplies one when nothing else has.
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton<ITicketStore, DistributedCacheTicketStore>();

            builder.AddRefreshedAccessTokens();
            builder.Services
                .AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
                .Configure<ITicketStore>((options, store) => options.SessionStore = store);

            builder.Services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(options =>
                {
                    options.Cookie.Name = "GroupSplit.Auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || overPlainHttp
                        ? CookieSecurePolicy.SameAsRequest
                        : CookieSecurePolicy.Always;

                    // Below the realm's SSO idle timeout, so the cookie never
                    // outlives the Keycloak session it was minted from.
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                    options.SlidingExpiration = true;

                    // Nothing refreshes here any more: the tokens live beside the
                    // ticket, so keeping them current never rewrites the cookie and is not
                    // confined to this point in a request. Sign-out still has to reach
                    // them, or a refresh token outlives its session.
                    options.Events.OnSigningOut = OnSigningOut;

                    options.ConfigureOptions();
                })
                .AddKeycloakOpenIdConnect(
                    serviceName: "keycloak",
                    realm: "group-split",
                    options =>
                    {
                        options.ClientId = "web-app";
                        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.ResponseType = OpenIdConnectResponseType.Code;

                        // Off, deliberately: SaveTokens puts the access and refresh
                        // tokens in the ticket, and the ticket is what the cookie is made
                        // of. Keeping them out of the browser would then rest on the ticket
                        // store staying configured. They live in ServerSideTokenStore,
                        // which a cookie cannot carry at all.
                        //
                        // The id token is the exception, and StoreTokensOnSignIn puts it
                        // back: it is not a credential for the API, and the sign-out reads
                        // it off the ticket to identify the session being ended.
                        options.SaveTokens = false;
                        options.UsePkce = true;

                        // The API provisions its user record from these claims.
                        options.Scope.Add("email");

                        options.TokenValidationParameters.NameClaimType = "name";
                        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

                        options.Events.OnRedirectToIdentityProvider = OnRedirectToIdentityProvider;

                        if (authority is null)
                        {
                            options.RequireHttpsMetadata = false;
                        }
                        else
                        {
                            // Service discovery cannot satisfy RequireHttpsMetadata.
                            options.Authority = authority;

                            // Defaults to the strictest setting the authority can support: an
                            // https authority gets metadata validation, an http one cannot have
                            // it. So putting TLS in front of Keycloak turns this on by itself.
                            options.RequireHttpsMetadata =
                                builder.Configuration.GetValue<bool?>("Keycloak:RequireHttpsMetadata")
                                ?? !overPlainHttp;
                        }

                        if (overPlainHttp)
                        {
                            // The handler marks the correlation and nonce cookies `Secure`
                            // unconditionally: the default form_post callback arrives as a
                            // cross-site POST, only SameSite=None rides on one, and SameSite=None
                            // is itself honoured only on a Secure cookie. Over plain HTTP the
                            // browser stores neither, and the callback then has no correlation
                            // cookie to check -- the sign-in fails with "Correlation failed".
                            //
                            // Lax is the strongest SameSite left, and it travels only on a
                            // top-level GET, so the callback has to stop being a form POST too.
                            // The code lands in the query string instead, which PKCE and the
                            // realm's 60s accessCodeLifespan are what make that acceptable.
                            options.ResponseMode = OpenIdConnectResponseMode.Query;

                            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                            options.NonceCookie.SameSite = SameSiteMode.Lax;
                            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                        }
                    });

            return builder;
        }

        /// <summary>
        /// Keeping the access token current: the library does the exchange, the store it
        /// works on is ours.
        /// </summary>
        /// <remarks>
        /// Three things the library gets right that are easy to get wrong by hand: one
        /// refresh token is exchanged once however many callers ask at the same moment,
        /// which matters because Keycloak rotates them one-time-use; a refused token is told
        /// apart from an unreachable authority, so a restart is not a mass sign-out; and the
        /// exchange is never retried. See <see cref="ServerSideTokenStore"/> for the store.
        /// </remarks>
        private void AddRefreshedAccessTokens()
        {
            builder.Services.AddOpenIdConnectAccessTokenManagement(options =>
            {
                // Replaced this far ahead of expiry so a token cannot die in flight,
                // between being read here and the API reading it at the other end.
                options.RefreshBeforeExpiration = TimeSpan.FromMinutes(1);
            });

            // Must come after the call above, whose own store and accessor are TryAdd
            // and are what this replaces. The pair is what makes the tokens reachable from
            // a circuit rather than only from a request.
            builder.Services.AddBlazorServerAccessTokenManagement<ServerSideTokenStore>();

            // AddServiceDefaults puts the standard resilience handler on every client,
            // and this is the one that must not have it: it retries a POST on a timeout or
            // 5xx, and retrying an exchange the realm did process presents a token it has
            // just retired -- answered invalid_grant, which ends the session. The library's
            // own resiliency, which knows the difference, takes its place.
            builder.Services
                .AddHttpClient(ClientCredentialsTokenManagementDefaults.BackChannelHttpClientName)
                .RemoveAllResilienceHandlers()
                .AddDefaultAccessTokenResiliency();

            // Chained rather than assigned: the library configures these events too,
            // and whoever assigns last silently discards the other. PostConfigure runs
            // after every Configure, so it is the only place that holds.
            builder.Services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    var validated = options.Events.OnTokenValidated;

                    options.Events.OnTokenValidated = async context =>
                    {
                        await validated(context);
                        await StoreTokensOnSignIn(context);
                    };
                });
        }
    }

    /// <summary>
    /// Names the session and writes its first set of tokens, at the one moment both are in
    /// hand. Everything afterwards reads and refreshes what is stored here.
    /// </summary>
    private static async Task StoreTokensOnSignIn(TokenValidatedContext context)
    {
        var response = context.TokenEndpointResponse;

        if (context.Principal?.Identity is not ClaimsIdentity identity || response is null)
        {
            return;
        }

        SessionTokenKey.Mint(identity);

        // The one token the ticket still carries. The handler builds id_token_hint from
        // HttpContext.GetTokenAsync(SignOutScheme, "id_token"), which reads the ticket and
        // nowhere else -- so without this the sign-out sends no hint, Keycloak cannot tell
        // which session is ending, and the realm's own session survives a sign-out.
        if (!string.IsNullOrWhiteSpace(response.IdToken))
        {
            context.Properties?.StoreTokens([
                new AuthenticationToken
                {
                    Name = OpenIdConnectParameterNames.IdToken,
                    Value = response.IdToken
                }
            ]);
        }

        var expiresIn = int.TryParse(response.ExpiresIn, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            // Only RECOMMENDED by RFC 6749. A short assumption is safe because being wrong
            // costs one early refresh, where assuming a long life costs a dead token.
            : TimeSpan.FromMinutes(1);

        await context.HttpContext.RequestServices
            .GetRequiredService<IUserTokenStore>()
            .StoreTokenAsync(
                context.Principal,
                new UserToken
                {
                    AccessToken = AccessToken.Parse(response.AccessToken),
                    AccessTokenType = string.IsNullOrWhiteSpace(response.TokenType)
                        ? null
                        : AccessTokenType.Parse(response.TokenType),
                    ClientId = ClientId.Parse(context.Options.ClientId!),
                    Expiration = DateTimeOffset.UtcNow.Add(expiresIn),
                    RefreshToken = string.IsNullOrWhiteSpace(response.RefreshToken)
                        ? null
                        : RefreshToken.Parse(response.RefreshToken),
                    IdentityToken = string.IsNullOrWhiteSpace(response.IdToken)
                        ? null
                        : IdentityToken.Parse(response.IdToken),
                    Scope = string.IsNullOrWhiteSpace(response.Scope) ? null : Scope.Parse(response.Scope)
                });
    }

    /// <summary>
    /// Throws the session's tokens away. Left behind, a refresh token outlives the session
    /// it belonged to.
    /// </summary>
    /// <remarks>
    /// Internal so it can be tested without a sign-in.
    /// </remarks>
    internal static Task OnSigningOut(CookieSigningOutContext context) =>
        context.HttpContext.RequestServices
            .GetRequiredService<IUserTokenStore>()
            .ClearTokenAsync(context.HttpContext.User);

    private static Task OnRedirectToIdentityProvider(RedirectContext context)
    {
        // The client-side app cannot follow a redirect to Keycloak.
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.HandleResponse();
        }

        return Task.CompletedTask;
    }
}

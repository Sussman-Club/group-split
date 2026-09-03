using System.Security.Claims;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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

            // Holds the ticket -- and therefore the tokens -- server-side, leaving
            // the cookie small enough not to blow past Kestrel's header limit.
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSingleton<ITicketStore, DistributedCacheTicketStore>();
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

                        options.SaveTokens = true;
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
    }

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

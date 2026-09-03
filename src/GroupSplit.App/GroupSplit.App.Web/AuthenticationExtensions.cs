using System.Security.Claims;
using GroupSplit.App.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GroupSplit.App.Web;

public static class AuthenticationExtensions
{
    /// <summary>
    /// Keycloak serves registration from a sibling of the authorization endpoint
    /// rather than a request parameter, so sign-up rewrites the target.
    /// </summary>
    private const string AuthorizePath = "/protocol/openid-connect/auth";

    private const string RegistrationsPath = "/protocol/openid-connect/registrations";

    extension(WebApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddGroupSplitAuthentication()
        {
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
                    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
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

                        if (builder.Environment.IsDevelopment())
                        {
                            options.RequireHttpsMetadata = false;
                        }
                        else
                        {
                            // Service discovery cannot satisfy RequireHttpsMetadata.
                            options.Authority = builder.Configuration["Keycloak:Authority"]
                                ?? throw new InvalidOperationException(
                                    "Keycloak:Authority must be configured outside of development.");

                            // Defaults to the strictest setting the authority can support: an
                            // https authority gets metadata validation, an http one cannot have
                            // it. So putting TLS in front of Keycloak turns this on by itself.
                            options.RequireHttpsMetadata =
                                builder.Configuration.GetValue<bool?>("Keycloak:RequireHttpsMetadata")
                                ?? options.Authority.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
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

            return Task.CompletedTask;
        }

        if (context.Properties.Items.TryGetValue(IdentityApi.FlowProperty, out var flow)
            && flow == IdentityApi.RegisterFlow)
        {
            context.ProtocolMessage.IssuerAddress = context.ProtocolMessage.IssuerAddress
                .Replace(AuthorizePath, RegistrationsPath, StringComparison.Ordinal);
        }

        return Task.CompletedTask;
    }
}

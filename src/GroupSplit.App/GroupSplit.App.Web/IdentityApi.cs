using GroupSplit.App.Shared.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web;

public static class IdentityApi
{
    public static RouteGroupBuilder MapIdentity(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth");

        group.MapLogin();
        group.MapLogout();
        group.MapAccountDeleted();
        group.MapAccountConsole();

        return group;
    }

    private static void MapLogin(this RouteGroupBuilder group)
    {
        group.MapGet("/login", ([FromQuery] string? returnUrl, [FromQuery] bool? remember) =>
            Results.Challenge(
                ChallengeProperties(returnUrl, remember ?? false),
                [OpenIdConnectDefaults.AuthenticationScheme]));
    }

    /// <summary>
    /// Signs out of this app and of Keycloak.
    /// </summary>
    /// <remarks>
    /// A POST carrying an antiforgery token, never a GET. As a GET, any page anywhere could
    /// sign a person out with an image tag, and a sibling subdomain could do it with a form
    /// too, since the cookie is SameSite=Lax and that site is the same site. The app's sign-out
    /// buttons post it through <c>SignOutForm</c>; a GET is answered 405.
    /// </remarks>
    private static void MapLogout(this RouteGroupBuilder group)
    {
        group.MapPost("/logout", (HttpContext context) =>
                // The middleware validates and records the answer; it refuses nothing itself.
                // Form binding is what normally reads the answer, and nothing is bound here.
                context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: true }
                    ? Results.SignOut(
                        properties: new AuthenticationProperties { RedirectUri = "/" },
                        authenticationSchemes:
                        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.BadRequest())
            // Explicit for the same reason: without a form parameter the middleware would
            // not validate the token at all.
            .WithMetadata(new RequireAntiforgeryTokenAttribute());
    }

    /// <summary>
    /// Signs out of this app and nowhere else, after the account behind the session was
    /// deleted, and says so on the sign-in page.
    /// </summary>
    /// <remarks>
    /// The cookie scheme alone, not OpenID Connect. Deleting the account deleted its Keycloak
    /// login, and Keycloak ended every session the login held as it did -- so the end-session
    /// round trip <see cref="MapLogout"/> makes has nothing left to end, and sends the browser
    /// to an identity provider that may be the very thing that could not be reached. Signing
    /// out of the cookie also clears the session's stored tokens; see
    /// <c>AuthenticationExtensions.OnSigningOut</c>.
    /// </remarks>
    private static void MapAccountDeleted(this RouteGroupBuilder group)
    {
        // AuthRoutes.AccountDeleted, below this group's /auth.
        group.MapGet("/account-deleted", () => Results.SignOut(
            properties: new AuthenticationProperties
            {
                RedirectUri = AuthRoutes.Handoff(HandoffReason.AccountDeleted)
            },
            authenticationSchemes: [CookieAuthenticationDefaults.AuthenticationScheme]));
    }

    /// <summary>Redirects to Keycloak's account console.</summary>
    private static void MapAccountConsole(this RouteGroupBuilder group)
    {
        group.MapGet("/account", async (
            IOptionsMonitor<OpenIdConnectOptions> optionsMonitor,
            CancellationToken cancellationToken) =>
        {
            var options = optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);

            if (options.ConfigurationManager is null)
            {
                return Results.NotFound();
            }

            // Authority is Aspire's "https+http://keycloak/realms/..." service
            // discovery address, which only an HttpClient can resolve. The issuer
            // from the discovery document is the URL a browser can actually reach.
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            var issuer = configuration.Issuer;

            return string.IsNullOrWhiteSpace(issuer)
                ? Results.NotFound()
                : Results.Redirect($"{issuer.TrimEnd('/')}/account");
        })
        .RequireAuthorization();
    }

    /// <summary>
    /// What the sign-in carries across to Keycloak and back: where to land afterwards, and
    /// whether the person asked to stay signed in.
    /// </summary>
    /// <remarks>
    /// The tick is only recorded here. What it comes to mean is
    /// <see cref="AuthenticationExtensions.RememberSession"/>, on the way back in -- the
    /// cookie being written is the far side of this round trip, and nothing in between can
    /// be trusted to still hold the answer.
    /// </remarks>
    internal static AuthenticationProperties ChallengeProperties(string? returnUrl, bool remember)
    {
        var properties = new AuthenticationProperties { RedirectUri = ResolveReturnUrl(returnUrl) };

        if (remember)
        {
            properties.Items[AuthenticationExtensions.RememberMeItem] = bool.TrueString;
        }

        return properties;
    }

    /// <summary>
    /// Reduces a caller-supplied return URL to a safe local path: anything else here
    /// would bounce a freshly authenticated user to another origin.
    /// </summary>
    internal static string ResolveReturnUrl(string? returnUrl) =>
        LocalUrl.IsLocal(returnUrl) ? returnUrl : "/";
}

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

    private static void MapLogout(this RouteGroupBuilder group)
    {
        group.MapGet("/logout", () => Results.SignOut(
            properties: new AuthenticationProperties { RedirectUri = "/" },
            authenticationSchemes:
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
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
    /// Reduces a caller-supplied return URL to a safe local path: an absolute URL
    /// here would bounce a freshly authenticated user to another origin.
    /// </summary>
    internal static string ResolveReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
        {
            return "/";
        }

        // "//evil.example" and "/\evil.example" are absolute despite looking relative.
        if (returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return returnUrl;
    }
}

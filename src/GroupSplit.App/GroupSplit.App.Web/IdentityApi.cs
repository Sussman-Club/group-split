using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GroupSplit.App.Web;

public static class IdentityApi
{
    /// <summary>Asks the OIDC handler for Keycloak's registration flow.</summary>
    public const string FlowProperty = "gs_flow";

    public const string RegisterFlow = "register";

    public static RouteGroupBuilder MapIdentity(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth");

        group.MapLogin();
        group.MapRegister();
        group.MapLogout();
        group.MapAccountConsole();

        return group;
    }

    private static void MapRegister(this RouteGroupBuilder group)
    {
        group.MapGet("/register", ([FromQuery] string? returnUrl) =>
        {
            var properties = new AuthenticationProperties
            {
                RedirectUri = ResolveReturnUrl(returnUrl),
                Items =
                {
                    [FlowProperty] = RegisterFlow
                }
            };

            return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
    }

    private static void MapLogin(this RouteGroupBuilder group)
    {
        group.MapGet("/login", ([FromQuery] string? returnUrl) =>
        {
            var properties = new AuthenticationProperties { RedirectUri = ResolveReturnUrl(returnUrl) };

            return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
    }

    private static void MapLogout(this RouteGroupBuilder group)
    {
        group.MapGet("/logout", () => Results.SignOut(
            properties: new AuthenticationProperties { RedirectUri = "/" },
            authenticationSchemes:
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    }

    /// <summary>Resolved server-side so the authority is never sent to the client.</summary>
    private static void MapAccountConsole(this RouteGroupBuilder group)
    {
        group.MapGet("/account", (IOptionsMonitor<OpenIdConnectOptions> options) =>
        {
            var authority = options.Get(OpenIdConnectDefaults.AuthenticationScheme).Authority;

            return string.IsNullOrWhiteSpace(authority)
                ? Results.NotFound()
                : Results.Redirect($"{authority.TrimEnd('/')}/account");
        })
        .RequireAuthorization();
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

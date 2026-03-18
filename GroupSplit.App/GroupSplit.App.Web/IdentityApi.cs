using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace GroupSplit.App.Web;

public static class IdentityApi
{
    public static RouteGroupBuilder MapIdentity(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth");

        group.MapLogin();
        group.MapRegister();
        group.MapLogout();

        return group;
    }

    private static void MapRegister(this RouteGroupBuilder group)
    {
        group.MapGet("/register", () =>
        {
            var properties = new AuthenticationProperties
            {
                RedirectUri = "/",
                Items =
                {
                    ["prompt"] = "register"
                }
            };
            return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
    }

    private static void MapLogin(this RouteGroupBuilder group)
    {
        group.MapGet("/login", ([FromQuery] string? returnUrl) =>
        {
            var properties = new AuthenticationProperties { RedirectUri = returnUrl ?? "/" };
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
}
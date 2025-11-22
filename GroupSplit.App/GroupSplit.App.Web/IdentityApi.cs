using System.Security.Claims;
using GroupSplit.App.Web.Authentication;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

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

    extension(RouteGroupBuilder group)
    {
        private RouteGroupBuilder MapRegister()
        {
            group.MapPost("/register",
                async (RegisterRequest request, IIdentityClient client) =>
                {
                    await client.PostIdentityRegisterAsync(request);

                    var login = new LoginRequest { Email = request.Email, Password = request.Password };

                    var tokenResponse = await client.PostIdentityLoginAsync(
                        useCookies: false,
                        useSessionCookies: false,
                        login
                    );

                    var userInfo = new UserIdentityInfo(login.Email);
                    return SignIn(userInfo, tokenResponse.AccessToken);
                });
            return group;
        }

        private RouteGroupBuilder MapLogin()
        {
            group.MapPost("/login", async (
                LoginRequest login,
                IIdentityClient client
            ) =>
            {
                var tokenResponse = await client.PostIdentityLoginAsync(
                    useCookies: false,
                    useSessionCookies: false,
                    login
                );

                var userInfo = new UserIdentityInfo(login.Email);
                return SignIn(userInfo, tokenResponse.AccessToken);
            });
            return group;
        }

        private RouteGroupBuilder MapLogout()
        {
            group.MapPost("logout",
                    async context => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); })
                .RequireAuthorization();
            return group;
        }
    }

    private static IResult SignIn(UserIdentityInfo userInfo, string token)
    {
        return SignIn(userInfo.Email, token);
    }

    private static IResult SignIn(string userName, string token)
    {
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, userName));

        var properties = new AuthenticationProperties();

        properties.StoreTokens([
            new AuthenticationToken { Name = TokenNames.AccessToken, Value = token }
        ]);

        return Results.SignIn(new ClaimsPrincipal(identity),
            properties: properties,
            authenticationScheme: CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
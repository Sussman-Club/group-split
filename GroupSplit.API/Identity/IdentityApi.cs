using GroupSplit.Identity;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace GroupSplit.API.Identity;

public static class IdentityApi
{
    public static RouteGroupBuilder MapIdentity(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/identity");

        group.WithTags("Identity");

        group.MapIdentityApi<User>();
        group.MapExternalLoginApi();

        return group;
    }

    public static RouteGroupBuilder MapExternalLoginApi(this RouteGroupBuilder group)
    {
        group.MapPost("/token/{provider}",
            async Task<Results<Ok<AccessTokenResponse>, SignInHttpResult, ValidationProblem>>
            (
                string provider,
                ExternalUserInfo userInfo,
                UserManager<User> userManager,
                SignInManager<User> signInManager,
                IDataProtectionProvider dataProtectionProvider
            ) =>
            {
                var protector = dataProtectionProvider.CreateProtector(provider);

                var providerKey = protector.Unprotect(userInfo.ProviderKey);

                var user = await userManager.FindByLoginAsync(provider, providerKey);

                var result = IdentityResult.Success;

                if (user is null)
                {
                    user = new User { UserName = userInfo.Username };

                    result = await userManager.CreateAsync(user);

                    if (result.Succeeded)
                    {
                        result = await userManager.AddLoginAsync(
                            user,
                            new UserLoginInfo(provider, providerKey, providerDisplayName: null)
                        );
                    }
                }

                if (result.Succeeded)
                {
                    var principal = await signInManager.CreateUserPrincipalAsync(user);

                    return TypedResults.SignIn(principal);
                }

                return TypedResults.ValidationProblem(
                    result.Errors.ToDictionary(e => e.Code, e => new[] { e.Description })
                );
            });

        return group;
    }
}
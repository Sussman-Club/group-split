using GroupSplit.API.Services;

namespace GroupSplit.API.Endpoints;

public static class UserApi
{
    public static RouteGroupBuilder MapUserApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/users")
            .RequireAuthorization();
        group.WithTags("Users");

        group.MapGetCurrentUser();
        
        return group;
    }

    private static RouteHandlerBuilder MapGetCurrentUser(this RouteGroupBuilder group)
    {
        return group.MapGet("/me", async (
            IUserService userService) =>
        {
            var user = await userService.GetCurrentUser();
            return Results.Ok(new
            {
                user.Id,
            });
        });
    }
}

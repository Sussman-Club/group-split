using GroupSplit.API.Services;

namespace GroupSplit.API.Endpoints;

public static class UserApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapUserApi()
        {
            var group = routes.MapGroup("/users")
                .RequireAuthorization();
            group.WithTags("Users");

            group.MapGetCurrentUser();
        
            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapGetCurrentUser()
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
}

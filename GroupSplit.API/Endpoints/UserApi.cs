using GroupSplit.API.Services;
using GroupSplit.Shared;

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
                var userInfo = new UserInfo(user.Id);
                return Results.Ok(userInfo);
            })
            .Produces<UserInfo>();
        }
    }
}

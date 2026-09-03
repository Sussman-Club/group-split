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
            return group.MapGet("/me", (
                    ICurrentUser currentUser) =>
                {
                    var user = currentUser.User;
                    var userInfo = new UserInfo(user.Id, user.FirstName, user.LastName, user.Email);
                    return Results.Ok(userInfo);
                })
                .WithName("GetCurrentUser")
                .Produces<UserInfo>();
        }
    }
}

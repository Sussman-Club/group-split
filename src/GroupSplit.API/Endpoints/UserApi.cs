using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Endpoints;

public static class UserApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapUserApi()
        {
            var group = routes.MapGroup("/users")
                .RequireAuthorization()
                .ProducesStandardProblems();
            group.WithTags("Users");

            group.MapGetCurrentUser();
            group.MapDeleteCurrentUser();

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

        private RouteHandlerBuilder MapDeleteCurrentUser()
        {
            return group.MapDelete("/me", async (
                    ICurrentUser currentUser,
                    IAccountService accounts,
                    CancellationToken ct) =>
                {
                    // Self-service is this route passing its own caller's id, and nothing
                    // more: the service itself will delete whichever account it is given.
                    var outstanding = await accounts.DeleteAccount(currentUser.User.Id, ct);

                    if (outstanding.Count == 0)
                        return Results.NoContent();

                    // Settling up first is the rule that already governs leaving a single
                    // group, so a refusal names the groups that are in the way instead of
                    // leaving someone to work out which of them it meant. They ride on the
                    // problem as an extension member, so the client branches on the code and
                    // reads the list, rather than on the shape of the body.
                    return Problems.Conflict(
                        ErrorCodes.AccountNotSettled,
                        "Settle up in every group before deleting your account.",
                        new Dictionary<string, object?>
                        {
                            [GroupSplit.Shared.ProblemDetails.OutstandingBalancesExtension] = outstanding
                        });
                })
                .WithName("DeleteCurrentUser")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}

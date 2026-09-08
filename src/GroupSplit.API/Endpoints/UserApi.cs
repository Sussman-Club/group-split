using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
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
            group.MapGetPosition();
            group.MapGetSettlementPlan();
            group.MapSettleWithPerson();
            group.MapGetSettlements();
            group.MapGetActivity();
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

        /// <summary>
        /// Where the caller stands across every group, which is the figure the home page
        /// leads with.
        /// </summary>
        /// <remarks>
        /// The one question the app could not answer before. Every balance was read a group
        /// at a time, so "am I up or down overall" meant opening each group in turn and
        /// adding up by hand -- and the home page led instead with what the person had paid,
        /// which is a number nobody is actually asking about.
        /// </remarks>
        private RouteHandlerBuilder MapGetPosition()
        {
            return group.MapGet("/me/position", async (
                    IGroupService groups,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await groups.GetPosition(ct));
                })
                .WithName("GetCurrentUserPosition")
                .Produces<UserPositionResponse>();
        }

        /// <summary>
        /// How to clear everything, in the fewest payments, across every group at once.
        /// </summary>
        /// <remarks>
        /// The minimised who-pays-whom has been computed for every group since balances
        /// existed and was rendered on no screen at all. This is that same arithmetic run
        /// over every group and then added up by person, which is the axis a payment
        /// actually has: owing the same friend in two groups is one payment, not two.
        /// </remarks>
        private RouteHandlerBuilder MapGetSettlementPlan()
        {
            return group.MapGet("/me/settlement-plan", async (
                    ISettlementService settlements,
                    CancellationToken ct) =>
                Results.Ok(await settlements.GetPlan(ct)))
                .WithName("GetSettlementPlan")
                .Produces<SettlementPlanResponse>();
        }

        /// <summary>
        /// Records one payment between the caller and one other person, wherever the debt
        /// between them lives.
        /// </summary>
        /// <remarks>
        /// The cross-group counterpart of <c>POST /groups/{id}/settle</c>. A balance belongs
        /// to a group and cannot be cleared from outside one, so behind a single action this
        /// writes one transfer per group -- in one save, because a payment that half-recorded
        /// would leave two groups disagreeing about whether it happened.
        /// </remarks>
        private RouteHandlerBuilder MapSettleWithPerson()
        {
            return group.MapPost("/me/settle", async (
                    SettleWithPersonRequest request,
                    ISettlementService settlements,
                    CancellationToken ct) =>
                Results.Ok(await settlements.SettleWithPerson(request, ct)))
                .WithName("SettleWithPerson")
                .Produces<SettleWithPersonResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// Every repayment the caller was party to, in any group, newest first.
        /// </summary>
        private RouteHandlerBuilder MapGetSettlements()
        {
            return group.MapGet("/me/settlements", async (
                    [AsParameters] PageRequest page,
                    ISettlementService settlements,
                    CancellationToken ct) =>
                {
                    var history = await settlements.GetHistory(ct);

                    return Results.Ok(await history
                        .OrderByDescending(settlement => settlement.DateTime)
                        .ThenByDescending(settlement => settlement.Id)
                        .ToPageAsync(page, ct));
                })
                .WithName("GetSettlements")
                .Produces<PagedResponse<SettlementResponse>>();
        }

        /// <summary>
        /// Everything that has happened anywhere the caller is, newest first: expenses
        /// whoever paid for them, and settlements.
        /// </summary>
        private RouteHandlerBuilder MapGetActivity()
        {
            return group.MapGet("/me/activity", async (
                    [AsParameters] PageRequest page,
                    ICurrentUser currentUser,
                    IGroupService groups,
                    CancellationToken ct) =>
                {
                    var activity = await groups.GetUserActivity(ct);

                    return Results.Ok(await activity
                        .OrderByDescending(transaction => transaction.DateTime)
                        .ThenByDescending(transaction => transaction.Id)
                        .SelectUserActivityDto(currentUser.User.Id)
                        .ToPageAsync(page, ct));
                })
                .WithName("GetCurrentUserActivity")
                .Produces<PagedResponse<UserActivityResponse>>();
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

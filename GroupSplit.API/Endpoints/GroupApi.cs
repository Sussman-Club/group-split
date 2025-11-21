using GroupSplit.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GroupSplit.API.Endpoints;

public static class GroupApi
{
    extension(IEndpointRouteBuilder routeBuilder)
    {
        public RouteGroupBuilder MapGroupApi()
        {
            var group = routeBuilder
                .MapGroup("/groups")
                .RequireAuthorization();

            group.WithTags("Groups");

            group.MapCreate();
            group.MapGetAllGroups();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapCreate()
        {
            return group.MapPost(string.Empty, async (
                [FromServices] IGroupService groupService,
                CancellationToken ct) =>
            {
                var createdGroup = await groupService.CreateGroup(ct);
                return Results.Ok(new { createdGroup.Id });
            });
        }

        private RouteHandlerBuilder MapGetAllGroups()
        {
            return group.MapGet(string.Empty, async (
                [FromServices] IGroupService groupService,
                CancellationToken ct) =>
            {
                var groups = await groupService.GetAllGroups(ct);
                return Results.Ok(groups);
            });
        }
    }
}
using GroupSplit.API.Services;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
                var groupInfo = new GroupInfo(createdGroup.Id);
                return Results.Ok(groupInfo);
            })
                .Produces<GroupInfo>();
        }

        private RouteHandlerBuilder MapGetAllGroups()
        {
            return group.MapGet(string.Empty, async (
                [FromServices] IGroupService groupService,
                CancellationToken ct) =>
            {
                var groups = await groupService.GetAllGroups(ct);
                var groupInfos = groups.Select(g => new GroupInfo(g.Id)).ToListAsync(cancellationToken: ct);
                return Results.Ok(groupInfos);
            })
                .Produces<GroupInfo[]>();
        }
    }
}
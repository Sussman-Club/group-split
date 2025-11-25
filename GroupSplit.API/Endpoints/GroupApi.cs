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
            group.MapGetGroupById();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapCreate()
        {
            return group.MapPost(string.Empty, async (
                    [FromBody] CreateGroupRequest request,
                    [FromServices] IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var createdGroup = await groupService.CreateGroup(ct);
                    var groupInfo = new GroupResponse(createdGroup.Id);
                    return Results.Ok(groupInfo);
                })
                .WithName("CreateGroup")
                .Produces<GroupResponse>();
        }

        private RouteHandlerBuilder MapGetAllGroups()
        {
            return group.MapGet(string.Empty, async (
                    [FromServices] IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var groups = await groupService.GetAllGroups(ct);
                    var groupInfos = groups.Select(g => new GroupResponse(g.Id)).ToListAsync(cancellationToken: ct);
                    return Results.Ok(groupInfos);
                })
                .WithName("GetAllGroups")
                .Produces<GroupResponse[]>();
        }

        private RouteHandlerBuilder MapGetGroupById()
        {
            return group.MapGet("{id}",
                    async (Guid id, [FromServices] IGroupService groupService, CancellationToken ct) =>
                    {
                        var group = await groupService.GetGroupById(id, ct);
                        if (group is null) return Results.NotFound();
                        var groupInfo = new GroupResponse(group.Id);
                        return Results.Ok(groupInfo);
                    })
                .WithName("GetGroupById")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
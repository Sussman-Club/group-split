using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
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
                    var createdGroup = await groupService.CreateGroup(request, ct);
                    var groupInfo = new GroupResponse(createdGroup.Id, createdGroup.Name, 1);
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
                    var groupResponses = await groups.SelectDto().ToListAsync(cancellationToken: ct);
                    return Results.Ok(groupResponses);
                })
                .WithName("GetAllGroups")
                .Produces<GroupResponse[]>();
        }

        private RouteHandlerBuilder MapGetGroupById()
        {
            return group.MapGet("{id:guid}",
                    async (Guid id, [FromServices] IGroupService groupService, CancellationToken ct) =>
                    {
                        var group = await groupService.GetGroupById(id, ct);
                        var groupResponse = await group.SelectDto().FirstOrDefaultAsync(ct);

                        if (groupResponse is null) return Results.NotFound();

                        return Results.Ok(groupResponse);
                    })
                .WithName("GetGroupById")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }

    extension(IQueryable<Group> groups)
    {
        private IQueryable<GroupResponse> SelectDto()
        {
            return from @group in groups
                select new GroupResponse(@group.Id, @group.Name, @group.Users.Count);
        }
    }
}
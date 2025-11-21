using GroupSplit.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GroupSplit.API.Endpoints;

public static class GroupApi
{
    public static RouteGroupBuilder MapGroupApi(this IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder
            .MapGroup("/groups")
            .RequireAuthorization();

        group.WithTags("Groups");

        group.MapCreate();
        group.MapGetAllGroups();

        return group;
    }

    private static RouteHandlerBuilder MapCreate(this RouteGroupBuilder group)
    {
        return group.MapPost(string.Empty, async (
            [FromServices] IGroupService groupService,
            CancellationToken ct) =>
        {
            var createdGroup = await groupService.CreateGroup(ct);
            return Results.Ok(new { createdGroup.Id });
        });
    }

    private static RouteHandlerBuilder MapGetAllGroups(this RouteGroupBuilder group)
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

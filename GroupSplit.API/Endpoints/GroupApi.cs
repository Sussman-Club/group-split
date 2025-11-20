using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Endpoints;

public static class GroupApi
{
    extension (IEndpointRouteBuilder routeBuilder) 
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

        private RouteHandlerBuilder MapCreate()
        {
            return routeBuilder.MapPost(string.Empty, async (
                [FromServices] AppDbContext context, 
                [FromServices] IUserService userService,
                CancellationToken ct) =>
            {
                var user = await userService.GetCurrentUser();

                var group = new Group
                {
                    Users = { user }
                };

                context.Add(group);
                await context.SaveChangesAsync(ct);
            });
        }

        private RouteHandlerBuilder MapGetAllGroups()
        {
            return routeBuilder.MapGet(string.Empty, async (
                [FromServices] AppDbContext context,
                [FromServices] IUserService userService,
                CancellationToken ct) =>
            {
                var user = await userService.GetCurrentUser();
                await context.Entry(user).Collection(u => u.Groups).LoadAsync();
                return Results.Ok(user.Groups.Select(g => new { g.Id, personal = user.PersonalGroup.Id == g.Id }));
            });
        }
    }
}

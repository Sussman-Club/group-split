using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
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
            group.MapGetGroup();
            group.MapUpdateGroup();
            group.MapGetGroupTransactions();
            group.MapGetGroupRules();
            group.MapGetMembers();
            group.MapAddMember();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapCreate()
        {
            return group.MapPost(string.Empty, async (
                    CreateGroupRequest request,
                    IGroupService groupService,
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
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var groups = await groupService.GetAllGroups(ct);
                    var groupResponses = await groups.SelectDto().ToListAsync(cancellationToken: ct);
                    return Results.Ok(groupResponses);
                })
                .WithName("GetGroups")
                .Produces<GroupResponse[]>();
        }

        private RouteHandlerBuilder MapGetGroup()
        {
            return group.MapGet("{id:guid}", async (
                    Guid id,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var group = await groupService.GetGroupById(id, ct);
                    var groupResponse = await group.SelectDto().FirstOrDefaultAsync(ct);

                    if (groupResponse is null) return Results.NotFound();

                    return Results.Ok(groupResponse);
                })
                .WithName("GetGroup")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
        
        private RouteHandlerBuilder MapUpdateGroup()
        {
            return group.MapPatch("{id:guid}", async (
                Guid id,
                JsonPatchDocument<CreateGroupRequest> patchDocument,
                IGroupService groupService,
                CancellationToken ct) =>
            {
                var groups = await groupService.GetGroupById(id, ct);
                var groupUpdateRequest = await (from t in groups
                    select new CreateGroupRequest
                    {
                        Name = t.Name
                    }).FirstOrDefaultAsync(ct);
                
                patchDocument.ApplyTo(groupUpdateRequest);

                await groupService.UpdateGroup(id, groupUpdateRequest, ct);

                return Results.Ok();
            }).WithName("UpdateGroup");
        }

        private RouteHandlerBuilder MapGetGroupTransactions()
        {
            return group.MapGet("{id:guid}/transactions", async (
                    Guid id,
                    [AsParameters] TransactionFilter filter,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transactions = await transactionService.List(ct);
                    var transactionResponses = await transactions
                        .Where(x => x.RuleVersion.Rule.Group.Id == id)
                        .ApplyFilter(filter).SelectDto().ToListAsync(ct);
                    return Results.Ok(transactionResponses);
                })
                .WithName("GetGroupTransactions")
                .Produces<TransactionResponse[]>();
        }

        private RouteHandlerBuilder MapGetGroupRules()
        {
            return group.MapGet("{id:guid}/rules", async (
                    Guid id,
                    IRulesService transactionService,
                    CancellationToken ct) =>
                {
                    var rules = await transactionService.List(ct);
                    var ruleResponses = await rules
                        .Where(x => x.Rule.Group.Id == id)
                        .Include(x => x.Rule)
                        .SelectDto()
                        .ToListAsync(ct);
                    return Results.Ok(ruleResponses);
                })
                .WithName("GetGroupRules")
                .Produces<RuleVersionResponse[]>();
        }

        private RouteHandlerBuilder MapGetMembers()
        {
            return group.MapGet("{id:guid}/members", async (
                    Guid id,
                    IGroupService groupService,
                    CancellationToken ct) =>
                    {
                        var members = (await groupService.GetGroupMembers(id)).SelectDto();
                        var userResponse = await members.ToListAsync(ct);
                        return userResponse is null ? Results.NotFound() : Results.Ok(userResponse);
                    })
                    .WithName("GetGroupMembers")
                    .Produces<UserInfo[]>()
                    .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapAddMember()
        {
            return group.MapPost("{id:guid}/members", async (
                    Guid id,
                    AddMemberRequest request,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    // Implementation for adding a member would go here
                    await groupService.AddGroupMembers(id, request);
                    return Results.Ok();
                })
                .WithName("AddGroupMember")
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

    extension(IQueryable<User> users)
    {
        private IQueryable<UserInfo> SelectDto()
        {
            return from user in users
                   select new UserInfo(user.Id, user.FirstName, user.LastName);
        }
    }

    extension(IQueryable<RuleVersion> ruleVersions)
    {
        private IQueryable<RuleVersionResponse> SelectDto()
        {
            return from ruleVersion in ruleVersions
                select new RuleVersionResponse(ruleVersion.Id, ruleVersion.Rule.Category);
        }
    }
}
using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
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
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Groups");

            group.MapCreate();
            group.MapGetAllGroups();
            group.MapGetGroup();
            group.MapUpdateGroup();
            group.MapGetGroupTransactions();
            group.MapGetGroupRules();
            group.MapGetMembers();
            group.MapAddMember();
            group.MapRemoveMember();
            group.MapGetGroupUserBalance();
            group.MapSettle();

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
                .Produces<GroupResponse>()
                .ProducesValidationProblem();
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

                    if (groupResponse is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

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
                    var groupUpdateRequest = await groupService.GetUpdateModel(id, ct);

                    if (groupUpdateRequest is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                    patchDocument.ApplyTo(groupUpdateRequest);

                    if (!PatchedModel.IsValid(groupUpdateRequest, out var invalid))
                        return invalid;

                    await groupService.UpdateGroup(id, groupUpdateRequest, ct);

                    var group = await groupService.GetGroupById(id, ct);
                    var groupResponse = await group.SelectDto().FirstOrDefaultAsync(ct);

                    return Results.Ok(groupResponse);
                })
                .WithName("UpdateGroup")
                .Produces<GroupResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound);
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
                    [AsParameters] RuleFilter filter,
                    IRuleService transactionService,
                    CancellationToken ct) =>
                {
                    var rules = await transactionService.List(ct);
                    var ruleResponses = await rules
                        .Where(x => x.Rule.Group.Id == id)
                        .Include(x => x.Rule)
                        .ApplyFilter(filter)
                        .SelectDto()
                        .ToListAsync(ct);
                    return Results.Ok(ruleResponses);
                })
                .WithName("GetGroupRules")
                .Produces<RuleVersionResponse[]>();
        }

        // A group the caller is not in answers with an empty list, like the transaction and
        // rule listings above, so there is no 404 to declare here.
        private RouteHandlerBuilder MapGetMembers()
        {
            return group.MapGet("{id:guid}/members", async (
                    Guid id,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var members = (await groupService.GetGroupMembers(id, ct)).SelectDto();
                    var userResponse = await members.ToListAsync(ct);
                    return Results.Ok(userResponse);
                })
                .WithName("GetGroupMembers")
                .Produces<UserInfo[]>();
        }

        private RouteHandlerBuilder MapAddMember()
        {
            return group.MapPost("{id:guid}/members", async (
                    Guid id,
                    AddMemberRequest request,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var group = await groupService.AddGroupMembers(id, request, ct);
                    var groupResponse = await group.SelectDto().FirstOrDefaultAsync(ct);

                    if (groupResponse is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                    return Results.Ok(groupResponse);
                })
                .WithName("AddGroupMember")
                .Produces<GroupResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapRemoveMember()
        {
            return group.MapDelete("{groupId:guid}/members/{userId}", async (
                    Guid groupId,
                    Guid userId,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var group = await groupService.RemoveGroupMember(groupId, userId, ct);
                    var groupResponse = await group.SelectDto().FirstOrDefaultAsync(ct);

                    if (groupResponse is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                    return Results.Ok(groupResponse);
                })
                .WithName("RemoveGroupMember")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapGetGroupUserBalance()
        {
            return group.MapGet("{groupId:guid}/balances", async (
                    Guid groupId,
                    IGroupService groupService,
                    IDebtCalculationService debtCalculator,
                    CancellationToken ct) =>
            {
                var balances = await groupService.GetGroupNetBalance(groupId, ct);
                var balanceResponse = await balances.ToArrayAsync(ct);

                // Every group has at least its creator, so no rows means the group is not
                // one of the caller's. Left to the calculator, the caller's absence from the
                // settlement would surface as a bug rather than as this.
                if (balanceResponse.Length == 0)
                    return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                var groupUserBalance = await debtCalculator.GetUserBalance(balanceResponse);

                return Results.Ok(groupUserBalance);
            })
                .WithName("GetGroupUserBalance")
                .Produces<UserGroupBalanceResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapSettle()
        {
            return group.MapPost("{groupId:guid}/settle", async (
                Guid groupId,
                SettleRequest request,
                IGroupService groupService,
                CancellationToken ct) =>
            {
                await groupService.Settle(groupId, request, ct);
                return Results.NoContent();
            })
            .WithName("SettleGroupDebts")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
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
                select new UserInfo(user.Id, user.FirstName, user.LastName, user.Email);
        }
    }
}

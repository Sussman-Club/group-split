using GroupSplit.API.Errors;
using GroupSplit.API.Extensions;
using GroupSplit.API.Services;
using GroupSplit.Data;
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
            group.MapGetGroupTransactionsSummary();
            group.MapGetActivity();
            group.MapGetMembers();
            group.MapLeave();
            group.MapRemoveMember();
            group.MapGetInvitations();
            group.MapInvite();
            group.MapWithdrawInvitation();
            group.MapGetGroupUserBalance();
            group.MapSettle();
            group.MapArchive();
            group.MapUnarchive();

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
                    ICurrentUser currentUser,
                    AppDbContext context,
                    CancellationToken ct) =>
                {
                    var groups = await groupService.GetAllGroups(ct);
                    var groupResponses = await groups.SelectDto(context, currentUser.User.Id)
                        .ToListAsync(cancellationToken: ct);
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
                    ICurrentUser currentUser,
                    AppDbContext context,
                    CancellationToken ct) =>
                {
                    var group = await groupService.GetGroupById(id, ct);
                    var groupResponse = await group.SelectDto(context, currentUser.User.Id).FirstOrDefaultAsync(ct);

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
                    ICurrentUser currentUser,
                    AppDbContext context,
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
                    var groupResponse = await group.SelectDto(context, currentUser.User.Id).FirstOrDefaultAsync(ct);

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
                    [AsParameters] SortRequest sort,
                    [AsParameters] PageRequest page,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transactions = await transactionService.List(ct);
                    return Results.Ok(await transactions
                        .Where(x => x.GroupId == id)
                        .ToTransactionPageAsync(filter, sort, page, ct));
                })
                .WithName("GetGroupTransactions")
                .Produces<PagedResponse<TransactionResponse>>()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        private RouteHandlerBuilder MapGetGroupTransactionsSummary()
        {
            return group.MapGet("{id:guid}/transactions/summary", async (
                    Guid id,
                    [AsParameters] TransactionFilter filter,
                    ITransactionService transactionService,
                    CancellationToken ct) =>
                {
                    var transactions = await transactionService.List(ct);
                    return Results.Ok(await transactions
                        .Where(x => x.GroupId == id)
                        .ToSummaryAsync(filter, ct));
                })
                .WithName("GetGroupTransactionsSummary")
                .Produces<TransactionSummaryResponse>();
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

        /// <summary>
        /// Asks people to join, by email. There is no endpoint that puts somebody in a group
        /// without their say-so any more: the one that did looked the address up and, when
        /// nothing matched, dropped it and reported success.
        /// </summary>
        private RouteHandlerBuilder MapInvite()
        {
            return group.MapPost("{id:guid}/invitations", async (
                    Guid id,
                    AddMemberRequest request,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    var pending = await invitations.Invite(id, request, ct);
                    return Results.Ok(pending);
                })
                .WithName("InviteToGroup")
                .Produces<GroupInvitationResponse[]>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapGetInvitations()
        {
            return group.MapGet("{id:guid}/invitations", async (
                    Guid id,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await invitations.ForGroup(id, ct));
                })
                .WithName("GetGroupInvitations")
                .Produces<GroupInvitationResponse[]>();
        }

        private RouteHandlerBuilder MapWithdrawInvitation()
        {
            return group.MapDelete("{id:guid}/invitations/{invitationId:guid}", async (
                    Guid id,
                    Guid invitationId,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    await invitations.Withdraw(id, invitationId, ct);
                    return Results.NoContent();
                })
                .WithName("WithdrawGroupInvitation")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// The caller taking themselves out. A separate route from removing a member because
        /// it is a separate act: that one is done to you, this one by you, and only this one
        /// is allowed to name yourself.
        /// </summary>
        private RouteHandlerBuilder MapLeave()
        {
            return group.MapDelete("{id:guid}/members/me", async (
                    Guid id,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    await groupService.Leave(id, ct);
                    return Results.NoContent();
                })
                .WithName("LeaveGroup")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        private RouteHandlerBuilder MapGetActivity()
        {
            return group.MapGet("{id:guid}/activity", async (
                    Guid id,
                    [AsParameters] SortRequest sort,
                    [AsParameters] PageRequest page,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var activity = await groupService.GetGroupActivity(id, ct);

                    return Results.Ok(await activity
                        .ApplySort(sort, ActivitySort)
                        .SelectActivityDto()
                        .ToPageAsync(page, ct));
                })
                .WithName("GetGroupActivity")
                .Produces<PagedResponse<GroupActivityResponse>>()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        private RouteHandlerBuilder MapRemoveMember()
        {
            return group.MapDelete("{groupId:guid}/members/{userId:guid}", async (
                    Guid groupId,
                    Guid userId,
                    IGroupService groupService,
                    ICurrentUser currentUser,
                    AppDbContext context,
                    CancellationToken ct) =>
                {
                    var group = await groupService.RemoveGroupMember(groupId, userId, ct);
                    var groupResponse = await group.SelectDto(context, currentUser.User.Id).FirstOrDefaultAsync(ct);

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

        /// <summary>
        /// Answers with the group, so a client can put the new state straight into the list
        /// it is already holding rather than reading the whole thing again.
        /// </summary>
        private RouteHandlerBuilder MapArchive()
        {
            return group.MapPost("{id:guid}/archive", async (
                    Guid id,
                    IGroupService groupService,
                    ICurrentUser currentUser,
                    AppDbContext context,
                    CancellationToken ct) =>
                {
                    var archived = await groupService.Archive(id, ct);
                    var groupResponse = await archived.SelectDto(context, currentUser.User.Id).FirstOrDefaultAsync(ct);

                    if (groupResponse is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                    return Results.Ok(groupResponse);
                })
                .WithName("ArchiveGroup")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapUnarchive()
        {
            return group.MapDelete("{id:guid}/archive", async (
                    Guid id,
                    IGroupService groupService,
                    ICurrentUser currentUser,
                    AppDbContext context,
                    CancellationToken ct) =>
                {
                    var unarchived = await groupService.Unarchive(id, ct);
                    var groupResponse = await unarchived.SelectDto(context, currentUser.User.Id).FirstOrDefaultAsync(ct);

                    if (groupResponse is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                    return Results.Ok(groupResponse);
                })
                .WithName("UnarchiveGroup")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }

    extension(IQueryable<Group> groups)
    {
        /// <summary>
        /// <paramref name="context"/> says whose list this is being read for: archiving is
        /// personal, so IsArchive is true only when that person archived it. Reading it off
        /// the group would tell every member what one of them had tidied away.
        /// </summary>
        private IQueryable<GroupResponse> SelectDto(AppDbContext context, Guid userId)
        {
            return from @group in groups
                join membership in context.Set<GroupMembership>()
                    on new { GroupId = @group.Id, UserId = userId }
                    equals new { membership.GroupId, membership.UserId }
                select new GroupResponse(@group.Id, @group.Name, @group.Users.Count,
                    membership.ArchivedAt != null);
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

    extension(IQueryable<Transaction> activity)
    {
        /// <summary>
        /// One row per thing that happened, whichever kind it was. The kind is read off the
        /// type rather than a column: EF knows which leaf each row is, and a <c>Kind</c>
        /// beside the discriminator would be a second answer to the same question.
        /// </summary>
        private IQueryable<GroupActivityResponse> SelectActivityDto()
        {
            return from transaction in activity
                select new GroupActivityResponse
                {
                    Id = transaction.Id,
                    Kind = transaction is Transfer ? ActivityKind.Transfer : ActivityKind.Expense,
                    Name = transaction.Name,
                    Description = transaction.Description,
                    Amount = transaction.Amount,
                    DateTime = transaction.DateTime,
                    PaidByUserId = transaction.UserId,
                    PaidByUserName = transaction.User.FirstName +
                                     (transaction.User.LastName != null ? " " + transaction.User.LastName : ""),
                    // A transfer has exactly one split, to whoever was paid, and that is
                    // what makes it a transfer. On an expense the shares say who carried it,
                    // and there is no single other party to name.
                    PaidToUserId = transaction is Transfer
                        ? transaction.Splits.Select(split => (Guid?)split.UserId).FirstOrDefault()
                        : null,
                    PaidToUserName = transaction is Transfer
                        ? transaction.Splits.Select(split =>
                            split.User.FirstName +
                            (split.User.LastName != null ? " " + split.User.LastName : "")).FirstOrDefault()
                        : null,
                    Category = transaction is Expense && ((Expense)transaction).Category != null
                        ? ((Expense)transaction).Category!.Name
                        : null
                };
        }
    }

    /// <summary>
    /// What a group's history can be ordered by. Date first, because a history is read as
    /// one, and the id breaks ties so a page does not show a row twice.
    /// </summary>
    internal static readonly SortMap<Transaction> ActivitySort = new SortMap<Transaction>()
        .Key("dateTime", transaction => transaction.DateTime, defaultDescending: true)
        .Key("amount", transaction => transaction.Amount, defaultDescending: true)
        .Key("name", transaction => transaction.Name)
        .Default("dateTime")
        .TieBreak(transaction => transaction.Id);
}

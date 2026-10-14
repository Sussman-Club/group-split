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
            group.MapGetGroupTransactionsSummary();
            group.MapGetActivity();
            group.MapGetMembers();
            group.MapGetPastMembers();
            group.MapLeave();
            group.MapRemoveMember();
            group.MapGetGuests();
            group.MapAddGuests();
            group.MapIssueGuestLink();
            group.MapRevokeGuestLink();
            group.MapGetInvitations();
            group.MapInvite();
            group.MapWithdrawInvitation();
            group.MapGetJoinLinks();
            group.MapCreateJoinLink();
            group.MapRevokeJoinLinks();
            group.MapGetGroupUserBalance();
            group.MapSettle();
            group.MapSettleUp();
            group.MapRecordRepayment();
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
                    var groupInfo = new GroupResponse(createdGroup.Id, createdGroup.Name, 1)
                    {
                        Currency = createdGroup.Currency,
                        Description = createdGroup.Description,
                        DefaultSplitRuleId = createdGroup.DefaultSplitRuleId,
                        DefaultSplitRuleName = createdGroup.DefaultSplitRule?.Name,
                        DefaultPeriod = createdGroup.DefaultPeriod
                    };
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
                    var groupResponses = await groupService.Describe(groups)
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
                    CancellationToken ct) =>
                {
                    var group = await groupService.GetGroupById(id, ct);
                    var groupResponse = await groupService.Describe(group).FirstOrDefaultAsync(ct);

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

                    patchDocument.ApplyOrRefuse(groupUpdateRequest);

                    // Null in the model means "the patch said nothing about it", which keeps
                    // the default a caller from before the field existed never knew about.
                    // A patch that names the field and leaves it null is asking for no
                    // default at all, and every group divides by one -- so it is refused
                    // rather than read as silence and quietly ignored.
                    if (patchDocument.Touches("/defaultSplitRuleId") && groupUpdateRequest.DefaultSplitRuleId is null)
                        throw new ValidationException(ErrorCodes.SplitRuleInvalid,
                            "A group always has a default rule. Name the one to use instead.");

                    if (!PatchedModel.IsValid(groupUpdateRequest, out var invalid))
                        return invalid;

                    await groupService.UpdateGroup(id, groupUpdateRequest, ct);

                    var group = await groupService.GetGroupById(id, ct);
                    var groupResponse = await groupService.Describe(group).FirstOrDefaultAsync(ct);

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
                    var transactions = await transactionService.InGroup(id, ct);
                    return Results.Ok(await transactions.ToTransactionPageAsync(filter, sort, page, ct));
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
                    var transactions = await transactionService.InGroup(id, ct);
                    return Results.Ok(await transactions.ToSummaryAsync(filter, ct));
                })
                .WithName("GetGroupTransactionsSummary")
                .Produces<TransactionSummaryResponse>();
        }

        /// <summary>
        /// Everybody the group can point at: its members, and the people it has invited and
        /// is waiting on, the latter marked as such.
        /// </summary>
        /// <remarks>
        /// One listing rather than two, because every screen that offers a choice of person
        /// -- who paid, whose share, who a rule names -- reads this one, and an invitee is
        /// choosable in all of them. A second endpoint for them would mean every such screen
        /// merging two lists and inventing its own opinion about which people go in which.
        /// <para>
        /// A group the caller is not in answers with an empty list, like the transaction and
        /// rule listings above, so there is no 404 to declare here.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapGetMembers()
        {
            return group.MapGet("{id:guid}/members", async (
                    Guid id,
                    IGroupService groupService,
                    IGroupParticipants participants,
                    CancellationToken ct) =>
                {
                    var members = participants.Describe(await groupService.GetGroupMembers(id, ct), id);
                    var userResponse = await members.ToListAsync(ct);
                    return Results.Ok(userResponse);
                })
                .WithName("GetGroupMembers")
                .Produces<UserInfo[]>();
        }

        /// <summary>
        /// People who have left but remain in the group's historical payments or shares.
        /// </summary>
        /// <remarks>
        /// This is intentionally separate from the participant endpoint. Past members can
        /// explain an old entry, but cannot be selected for a new expense, split rule, or
        /// settlement.
        /// </remarks>
        private RouteHandlerBuilder MapGetPastMembers()
        {
            return group.MapGet("{id:guid}/past-members", async (
                    Guid id,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var pastMembers = await groupService.GetGroupPastMembers(id, ct);
                    var userResponse = await groupService.DescribePastMembers(pastMembers, id)
                        .ToListAsync(ct);
                    return Results.Ok(userResponse);
                })
                .WithName("GetGroupPastMembers")
                .Produces<UserInfo[]>();
        }

        /// <summary>The group's guests, links included. Members only.</summary>
        private RouteHandlerBuilder MapGetGuests()
        {
            return group.MapGet("{id:guid}/guests", async (
                    Guid id,
                    IGuestService guests,
                    CancellationToken ct) => Results.Ok(await guests.ForGroup(id, ct)))
                .WithName("GetGroupGuests")
                .Produces<GroupGuestResponse[]>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Adds people to the group as guests, by name: somebody it shares costs with who has
        /// no account in it.
        /// </summary>
        /// <remarks>
        /// No link unless asked for. Most of the people a group splits with never sign up;
        /// the members record everything for them, and a link can be sent later if the real
        /// person wants in.
        /// </remarks>
        private RouteHandlerBuilder MapAddGuests()
        {
            return group.MapPost("{id:guid}/guests", async (
                    Guid id,
                    AddGuestsRequest request,
                    IGuestService guests,
                    CancellationToken ct) => Results.Ok(await guests.Add(id, request, ct)))
                .WithName("AddGroupGuests")
                .Produces<GroupGuestResponse[]>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .RateLimitedAs(ApiRateLimitPolicies.Invitations);
        }

        /// <summary>
        /// The guest's link, issuing one if there is none -- or a new one, replacing the old,
        /// when asked to.
        /// </summary>
        /// <remarks>
        /// Asking again without <c>replace</c> returns the link already out, so "send link"
        /// can always just copy -- unless it has expired, when a new one is issued in its
        /// place. Whoever opens it and claims it takes the guest's place.
        /// </remarks>
        private RouteHandlerBuilder MapIssueGuestLink()
        {
            return group.MapPost("{id:guid}/guests/{userId:guid}/link", async (
                    Guid id,
                    Guid userId,
                    IssueGuestLinkRequest? request,
                    IGuestService guests,
                    CancellationToken ct) =>
                    Results.Ok(await guests.IssueLink(id, userId, request?.Replace ?? false, ct)))
                .WithName("IssueGuestLink")
                .Produces<GroupGuestResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .RateLimitedAs(ApiRateLimitPolicies.Invitations);
        }

        /// <summary>Stops the guest's link. The guest stays.</summary>
        private RouteHandlerBuilder MapRevokeGuestLink()
        {
            return group.MapDelete("{id:guid}/guests/{userId:guid}/link", async (
                    Guid id,
                    Guid userId,
                    IGuestService guests,
                    CancellationToken ct) =>
                {
                    await guests.RevokeLink(id, userId, ct);
                    return Results.NoContent();
                })
                .WithName("RevokeGuestLink")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// Adds people as guests and issues each a link. Deprecated: add guests with
        /// <c>POST /groups/{id}/guests</c>, and send a link when one is wanted.
        /// </summary>
        private RouteHandlerBuilder MapInvite()
        {
            return group.MapPost("{id:guid}/invitations", async (
                    Guid id,
                    InviteToGroupRequest request,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    var pending = await invitations.Invite(id, request, ct);
                    return Results.Ok(pending);
                })
                .WithName("InviteToGroup")
                .Deprecated()
                .Produces<GroupInvitationResponse[]>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .RateLimitedAs(ApiRateLimitPolicies.Invitations);
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
                .Deprecated()
                .Produces<GroupInvitationResponse[]>();
        }

        /// <summary>
        /// Stops the link of the guest with that invitation id. Deprecated: use
        /// <c>DELETE /groups/{id}/guests/{userId}/link</c>. The guest stays, and nothing
        /// moves -- the counts in the answer are always zero.
        /// </summary>
        private RouteHandlerBuilder MapWithdrawInvitation()
        {
            return group.MapDelete("{id:guid}/invitations/{invitationId:guid}", async (
                    Guid id,
                    Guid invitationId,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await invitations.Withdraw(id, invitationId, ct));
                })
                .WithName("WithdrawGroupInvitation")
                .Deprecated()
                .Produces<InvitationClosedResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// The group's standing join links, which is one or none.
        /// </summary>
        /// <remarks>
        /// A collection, like the invitations beside it, and empty for a group nobody has
        /// made a link for. That is an ordinary state of a group and not a missing thing:
        /// the members page renders "make one" from an empty reading, where a 404 -- or a
        /// 204, which the generated client turns into a thrown exception -- would have it
        /// treat the commonest case as a failure.
        /// </remarks>
        private RouteHandlerBuilder MapGetJoinLinks()
        {
            return group.MapGet("{id:guid}/join-links", async (
                    Guid id,
                    IJoinLinkService links,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await links.ForGroup(id, ct));
                })
                .WithName("GetGroupJoinLinks")
                .Produces<GroupJoinLinkResponse[]>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Issues a link, putting out whatever the group had. Also how a link is reset:
        /// there is nothing to say about the old one once it is replaced.
        /// </summary>
        private RouteHandlerBuilder MapCreateJoinLink()
        {
            return group.MapPost("{id:guid}/join-links", async (
                    Guid id,
                    IJoinLinkService links,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await links.Create(id, ct));
                })
                .WithName("CreateGroupJoinLink")
                .Produces<GroupJoinLinkResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .RateLimitedAs(ApiRateLimitPolicies.Invitations);
        }

        /// <summary>
        /// Puts out every live link into the group, so the URLs already shared stop working.
        /// </summary>
        private RouteHandlerBuilder MapRevokeJoinLinks()
        {
            return group.MapDelete("{id:guid}/join-links", async (
                    Guid id,
                    IJoinLinkService links,
                    CancellationToken ct) =>
                {
                    await links.Revoke(id, ct);
                    return Results.NoContent();
                })
                .WithName("RevokeGroupJoinLinks")
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

        /// <summary>
        /// The group's ledger: everything that moved money in it, filtered by kind, date and
        /// text, each row carrying the reader's share and their balance as at that entry.
        /// </summary>
        /// <remarks>
        /// One listing where the app had two tabs. Expenses and Activity were the same rows
        /// with different filters -- one hid settlements, the other did not -- and splitting
        /// them made "which one was that in?" a real question while keeping the group's spend
        /// apart from the group's balance history.
        /// <para>
        /// The reason they were separated is real and is answered elsewhere: a settlement is
        /// not spending and must not land in a spending total. That is what
        /// <c>{id}/transactions/summary</c> is for -- it counts expenses whatever this is
        /// filtered to -- and it is why the figure above the list is called <em>total
        /// spent</em> rather than <em>total</em>.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapGetActivity()
        {
            return group.MapGet("{id:guid}/activity", async (
                    Guid id,
                    [AsParameters] ActivityFilter filter,
                    [AsParameters] SortRequest sort,
                    [AsParameters] PageRequest page,
                    ICurrentUser currentUser,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var activity = await groupService.GetGroupActivity(id, ct);

                    var ledger = await activity
                        .ApplyFilter(filter)
                        .ApplySort(sort, ActivitySort)
                        .SelectLedgerDto(currentUser.User.Id)
                        .ToPageAsync(page, ct);

                    return Results.Ok(ledger with
                    {
                        Items = await activity.WithRunningBalances(currentUser.User.Id, ledger.Items, ct)
                    });
                })
                .WithName("GetGroupActivity")
                .Produces<PagedResponse<GroupLedgerEntryResponse>>()
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }

        private RouteHandlerBuilder MapRemoveMember()
        {
            return group.MapDelete("{groupId:guid}/members/{userId:guid}", async (
                    Guid groupId,
                    Guid userId,
                    IGroupService groupService,
                    CancellationToken ct) =>
                {
                    var group = await groupService.RemoveGroupMember(groupId, userId, ct);
                    var groupResponse = await groupService.Describe(group).FirstOrDefaultAsync(ct);

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
            // ?through=&utcOffset= gives the balances, and the plan that clears them, for
            // everything dated up to that day after the settlements made for it, and later
            // ones as far as they pay it down: what a settle dialog previews before it
            // settles a period.
            return group.MapGet("{groupId:guid}/balances", async (
                    Guid groupId,
                    [AsParameters] CutoffQuery cutoff,
                    ISettlementService settlements,
                    CancellationToken ct) =>
            {
                // Null when the group is not one of the caller's: every group has at least its
                // creator, so no rows means that. Left to the calculator, the caller's absence
                // from the settlement would surface as a bug rather than as this.
                var position = await settlements.GetPosition(groupId, cutoff.ToCutoff(), ct);

                return position is not null
                    ? Results.Ok(position)
                    : Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");
            })
                .WithName("GetGroupUserBalance")
                .Produces<UserGroupBalanceResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapSettle()
        {
            return group.MapPost("{groupId:guid}/settle", async (
                Guid groupId,
                SettleRequest request,
                ISettlementService settlements,
                CancellationToken ct) =>
            {
                await settlements.Settle(groupId, request, ct);
                return Results.NoContent();
            })
            .WithName("SettleGroupDebts")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Settles the caller's own position in the group: every repayment between them and
        /// the rest of it, recorded together.
        /// </summary>
        private RouteHandlerBuilder MapSettleUp()
        {
            return group.MapPost("{groupId:guid}/settle-up", async (
                    Guid groupId,
                    SettleUpRequest request,
                    ISettlementService settlements,
                    CancellationToken ct) =>
                Results.Ok(await settlements.SettleUp(groupId, request, ct)))
                .WithName("SettleUp")
                .Produces<SettleUpResponse>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// Records one repayment between two named members, whichever of them the caller is
        /// -- including neither.
        /// </summary>
        /// <remarks>
        /// Separate from settle and settle-up on purpose: both of those put the caller on one
        /// end, and a member saying something about two other people should have to ask for
        /// that by name.
        /// </remarks>
        private RouteHandlerBuilder MapRecordRepayment()
        {
            return group.MapPost("{groupId:guid}/repayments", async (
                    Guid groupId,
                    RecordRepaymentRequest request,
                    ISettlementService settlements,
                    CancellationToken ct) =>
                Results.Ok(await settlements.RecordRepayment(groupId, request, ct)))
                .WithName("RecordRepayment")
                .Produces<SettlementPayment>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
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
                    CancellationToken ct) =>
                {
                    var archived = await groupService.Archive(id, ct);
                    var groupResponse = await groupService.Describe(archived).FirstOrDefaultAsync(ct);

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
                    CancellationToken ct) =>
                {
                    var unarchived = await groupService.Unarchive(id, ct);
                    var groupResponse = await groupService.Describe(unarchived).FirstOrDefaultAsync(ct);

                    if (groupResponse is null)
                        return Problems.NotFound(ErrorCodes.GroupNotFound, "Group was not found.");

                    return Results.Ok(groupResponse);
                })
                .WithName("UnarchiveGroup")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
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

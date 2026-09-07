using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The invitee's side of an invitation: what has been sent to you, and your answer.
/// </summary>
/// <remarks>
/// Not under <c>/groups/{id}</c>, because these are the one thing about a group that
/// somebody who is not in it may do. Scoping them under the group would put every one of
/// them behind a membership check that, by definition, has not been passed yet.
/// </remarks>
public static class InvitationsApi
{
    extension(IEndpointRouteBuilder routes)
    {
        public RouteGroupBuilder MapInvitationsApi()
        {
            var group = routes.MapGroup("/invitations")
                .RequireAuthorization()
                .ProducesStandardProblems();

            group.WithTags("Invitations");

            group.MapMine();
            group.MapAccept();
            group.MapDecline();
            group.MapDescribeLink();
            group.MapAcceptLink();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        private RouteHandlerBuilder MapMine()
        {
            return group.MapGet(string.Empty, async (
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await invitations.Mine(ct));
                })
                .WithName("GetMyInvitations")
                .Produces<GroupInvitationResponse[]>();
        }

        /// <summary>
        /// Answers with the group, because accepting one is how somebody joins and the
        /// client's next move is to open it.
        /// </summary>
        private RouteHandlerBuilder MapAccept()
        {
            return group.MapPost("{id:guid}/accept", async (
                    Guid id,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    var joined = await invitations.Accept(id, ct);
                    return Results.Ok(new GroupResponse(joined.Id, joined.Name, joined.Users.Count));
                })
                .WithName("AcceptInvitation")
                .Produces<GroupResponse>()
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        private RouteHandlerBuilder MapDecline()
        {
            return group.MapPost("{id:guid}/decline", async (
                    Guid id,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    await invitations.Decline(id, ct);
                    return Results.NoContent();
                })
                .WithName("DeclineInvitation")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// What the group behind a shared link is, for the page somebody lands on after
        /// following one.
        /// </summary>
        /// <remarks>
        /// The token is in the path because it is in the URL somebody was sent; there is
        /// nowhere else for it to be. It is a bearer credential all the same, so this
        /// answers nothing an invitee could not learn by using it: the group's name, how
        /// many are in it, and who made the link.
        /// <para>
        /// Authenticated, like everything else here. The join needs an account, so a page
        /// that named the group before sign-in would only be naming it a moment earlier, at
        /// the cost of a second door into the API that needs no credentials.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapDescribeLink()
        {
            return group.MapGet("links/{token}", async (
                    string token,
                    IJoinLinkService links,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await links.Describe(token, ct));
                })
                .WithName("GetJoinLink")
                .Produces<JoinLinkResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }

        /// <summary>
        /// Joins the group the link names. Answering twice is answering the same, which is
        /// what a URL that lives in a chat thread is going to be asked to do.
        /// </summary>
        private RouteHandlerBuilder MapAcceptLink()
        {
            return group.MapPost("links/{token}/accept", async (
                    string token,
                    IJoinLinkService links,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await links.Accept(token, ct));
                })
                .WithName("AcceptJoinLink")
                .Produces<JoinedGroupResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}

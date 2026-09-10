using GroupSplit.API.Errors;
using GroupSplit.API.Services;
using GroupSplit.Shared;

namespace GroupSplit.API.Endpoints;

/// <summary>
/// The invitee's side of an invitation: what a link you were sent leads to, and your answer.
/// </summary>
/// <remarks>
/// Not under <c>/groups/{id}</c>, because these are the one thing about a group that
/// somebody who is not in it may do. Scoping them under the group would put every one of
/// them behind a membership check that, by definition, has not been passed yet.
/// <para>
/// Everything here is addressed by token, and there is no listing. An invitation used to be
/// an email address, so an invitee could be shown every group waiting on them; it is a named
/// person and a link now, with no account to hang such a list from -- opening the link is how
/// somebody learns of it. That is the trade the group made for being able to invite people
/// whose address they do not have.
/// </para>
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
            group.MapDescribeInvitation();
            group.MapClaim();
            group.MapDecline();
            group.MapDescribeLink();
            group.MapAcceptLink();

            return group;
        }
    }

    extension(RouteGroupBuilder group)
    {
        /// <summary>
        /// The invitations the caller has opened and not yet answered.
        /// </summary>
        /// <remarks>
        /// The route that used to answer "every invitation sent to my address", answering a
        /// question it can still ask: every invitation whose link I have opened. Opening one
        /// is what puts it here, so somebody who has lost the message the link arrived in
        /// has a way back to it -- which without this they did not.
        /// <para>
        /// It carries the tokens. Holding one is what put the row here, so there is nothing
        /// in the answer the caller has not already had in their hands.
        /// </para>
        /// </remarks>
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
        /// What a personal invitation link leads to, without claiming it.
        /// </summary>
        /// <remarks>
        /// The token is in the path because it is in the URL somebody was sent; there is
        /// nowhere else for it to be. It answers nothing a claimer could not learn a moment
        /// later -- the group's name, its size, who asked, and which person the invitation
        /// was made out to -- and in particular nothing about the money, since a link can be
        /// forwarded and what is recorded against that person is the group's business.
        /// <para>
        /// Authenticated, like everything else here. Claiming needs an account, so a page
        /// that named the group before sign-in would only be naming it a moment earlier, at
        /// the cost of a second door into the API that needs no credentials.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapDescribeInvitation()
        {
            return group.MapGet("claims/{token}", async (
                    string token,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await invitations.Describe(token, ct));
                })
                .WithName("GetInvitationClaim")
                .Produces<InvitationClaimResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Claims it: the caller joins the group and becomes the person it names.
        /// </summary>
        /// <remarks>
        /// Answers with the group, because claiming is how somebody joins and the client's
        /// next move is to open it -- and with what came along, since this is the one moment
        /// a position changes hands. Nothing is re-divided and no amount changes, but a
        /// balance has moved onto the claimer's own account and the app should say so.
        /// <para>
        /// The link works once. Claiming removes the invitation the token lives on, so a
        /// second holder of a forwarded link gets the same 404 a mistyped one does.
        /// </para>
        /// </remarks>
        private RouteHandlerBuilder MapClaim()
        {
            return group.MapPost("claims/{token}", async (
                    string token,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await invitations.Claim(token, ct));
                })
                .WithName("ClaimInvitation")
                .Produces<InvitationClaimedResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// Turns it down, and says what became of anything the group had already recorded
        /// against the person.
        /// </summary>
        /// <remarks>
        /// A body where this used to answer 204. Naming somebody makes them a participant in
        /// the group's money straight away -- that is the point of it, since the spending
        /// that prompted the invitation is already happening -- so a decline can be the end
        /// of somebody who was carrying shares. Those cannot silently disappear: a group's
        /// balances add up only because every share belongs to somebody in the listing. They
        /// go to one member, named here, and no amount changes.
        /// </remarks>
        private RouteHandlerBuilder MapDecline()
        {
            return group.MapPost("claims/{token}/decline", async (
                    string token,
                    IInvitationService invitations,
                    CancellationToken ct) =>
                {
                    return Results.Ok(await invitations.Decline(token, ct));
                })
                .WithName("DeclineInvitation")
                .Produces<InvitationClosedResponse>()
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        /// <summary>
        /// What the group behind a shared link is, for the page somebody lands on after
        /// following one.
        /// </summary>
        /// <remarks>
        /// The group's open door, and a lighter thing than the personal link above: it lets
        /// somebody in as themselves, claiming nothing, so it is reusable and expires rather
        /// than dying on first use.
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

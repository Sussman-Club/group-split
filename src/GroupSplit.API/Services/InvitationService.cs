using System.Buffers.Text;
using System.Security.Cryptography;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Asking somebody to join a group, and their answer.
/// </summary>
/// <remarks>
/// Adding a member used to be a write the group made about somebody else: an address was
/// looked up, and if an account had it, that account was in the group from that moment --
/// and if none had, the address was silently dropped and the person who typed it was told
/// the member had been added. Both halves are wrong. Joining a group is something a person
/// agrees to.
/// <para>
/// It stopped being an address at all. Inviting somebody names them and mints a link, which
/// the group sends however they actually talk to them; whoever opens that link and claims it
/// becomes that person. An address was the handle, the display name and the invitee's own
/// list all at once, and it limited a group to asking people whose email they happened to
/// have.
/// </para>
/// <para>
/// The link is heavier than a join link and is treated so. That one lets somebody in as
/// themselves, claiming nothing; this one hands over a position in the group's ledger, so it
/// is single use -- claiming removes the row the token lives on -- and it says nothing about
/// the money before it is claimed.
/// </para>
/// </remarks>
public interface IInvitationService
{
    /// <summary>
    /// Invites each named person to a group the caller belongs to, minting a link for each.
    /// </summary>
    /// <returns>The group's pending invitations after the write, links included.</returns>
    Task<IReadOnlyList<GroupInvitationResponse>> Invite(Guid groupId, InviteToGroupRequest request,
        CancellationToken ct = default);

    /// <summary>Who a group is waiting on. Readable by its members.</summary>
    Task<IReadOnlyList<GroupInvitationResponse>> ForGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// What the link says about itself, for the page somebody lands on after opening one.
    /// Says nothing about what is recorded against the person; see
    /// <see cref="InvitationClaimResponse"/>.
    /// </summary>
    Task<InvitationClaimResponse> Describe(string token, CancellationToken ct = default);

    /// <summary>
    /// Claims one: the caller joins the group and becomes the person the invitation names.
    /// </summary>
    /// <remarks>
    /// The one moment a position changes hands. Everything recorded against the stand-in --
    /// its shares, and anything it was down as having paid for -- moves onto the caller's own
    /// account, with no amount changed, and the invitation and the stand-in both go. So the
    /// link works once: there is nothing left for a second holder of it to claim.
    /// </remarks>
    Task<InvitationClaimedResponse> Claim(string token, CancellationToken ct = default);

    /// <summary>
    /// Turns one down, on behalf of whoever holds the link. The row goes, the group may ask
    /// again, and anything recorded against the person is handed to a member -- see
    /// <see cref="InvitationClosedResponse"/>.
    /// </summary>
    Task<InvitationClosedResponse> Decline(string token, CancellationToken ct = default);

    /// <summary>Withdraws one the caller's group sent, on the same terms.</summary>
    Task<InvitationClosedResponse> Withdraw(Guid groupId, Guid invitationId,
        CancellationToken ct = default);
}

public sealed class InvitationService(
    ICurrentUser userContext,
    AppDbContext context,
    IGroupJoiner joiner,
    IGroupParticipants participants)
    : IInvitationService
{
    /// <summary>
    /// Bytes of randomness behind a token, before encoding, matching the join link's. This
    /// one guards more -- a position in a ledger rather than a seat in a group -- so it is
    /// certainly not less.
    /// </summary>
    private const int TokenBytes = 24;

    public async Task<IReadOnlyList<GroupInvitationResponse>> Invite(Guid groupId,
        InviteToGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inviter = userContext.User;
        var group = await MemberGroup(groupId, ct);

        var names = request.Names
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => name.Length > 0)
            .ToList();

        // Nothing usable in the list at all. Refused rather than answered with the group's
        // existing invitations, which would read as success for a request that asked for
        // something and got nothing.
        if (names.Count == 0)
            throw new ValidationException(ErrorCodes.GroupInvitationNoName,
                "An invitation needs a name to make it out to.");

        var invitedAt = DateTimeOffset.UtcNow;

        foreach (var name in names)
        {
            // The stand-in first, because the invitation is not merely a note that somebody
            // was asked: it is what makes this person somebody the group can give a share
            // to, which needs a row to give it to.
            //
            // One per invitation, and never an existing account -- there is no address to
            // recognise one by, and claiming is what attaches a real person to it.
            var participant = participants.StandInFor(name);

            context.Add(new GroupInvitation
            {
                Group = group,
                Name = name,
                Token = NewToken(),
                Participant = participant,
                InvitedBy = inviter,
                InvitedAt = invitedAt
            });
        }

        await context.SaveChangesAsync(ct);

        return await ForGroup(groupId, ct);
    }

    public async Task<IReadOnlyList<GroupInvitationResponse>> ForGroup(Guid groupId,
        CancellationToken ct = default)
    {
        var userId = userContext.User.Id;

        // Scoped to the caller's own groups, and that scoping is load-bearing now rather
        // than merely tidy: the rows carry the links, and a link is a claim on a position in
        // this group's ledger.
        //
        // Ordered before the projection, not after -- see the note on Describe.
        return await Project(
                from invitation in context.Set<GroupInvitation>()
                where invitation.GroupId == groupId &&
                      invitation.Group.Users.Any(user => user.Id == userId)
                orderby invitation.Name, invitation.InvitedAt
                select invitation)
            .ToListAsync(ct);
    }

    public async Task<InvitationClaimResponse> Describe(string token, CancellationToken ct = default)
    {
        var invitation = await Held(token, ct);

        var userId = userContext.User.Id;

        return await context.Set<GroupInvitation>()
            .Where(candidate => candidate.Id == invitation.Id)
            .Select(candidate => new InvitationClaimResponse(
                candidate.Id,
                candidate.GroupId,
                candidate.Group.Name,
                candidate.Group.Users.Count,
                candidate.Name,
                candidate.InvitedBy == null
                    ? null
                    : candidate.InvitedBy.FirstName + " " + candidate.InvitedBy.LastName,
                candidate.InvitedAt,
                candidate.Group.Users.Any(user => user.Id == userId)))
            .FirstAsync(ct);
    }

    public async Task<InvitationClaimedResponse> Claim(string token, CancellationToken ct = default)
    {
        var user = userContext.User;
        var invitation = await Held(token, ct);

        var group = await context.Set<Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == invitation.GroupId, ct);

        await joiner.Join(group, user, ct);

        // The position moves onto their own account: their shares become the claimer's, and
        // the expenses the stand-in was down as having paid for become theirs. No amount
        // changes, so every transaction still divides into exactly its own amount.
        var taken = await participants.HandOver(group.Id, invitation.ParticipantUserId, user.Id, ct);

        var standIn = invitation.ParticipantUserId;
        var name = invitation.Name;

        context.Remove(invitation);

        // The stand-in with it, now that nothing points at it. It exists to hold a position
        // until somebody claims it, and this is that moment; leaving it would leave a row
        // named after a person who is now in the group under their own account, turning up in
        // nothing and explaining nothing.
        var emptied = await context.Set<User>().FirstOrDefaultAsync(row => row.Id == standIn, ct);

        if (emptied is not null)
            context.Remove(emptied);

        await context.SaveChangesAsync(ct);

        return new InvitationClaimedResponse(
            group.Id,
            group.Name,
            group.Users.Count,
            name,
            taken.SharesMoved,
            taken.AmountOwed,
            taken.PaymentsMoved,
            taken.AmountPaid);
    }

    public async Task<InvitationClosedResponse> Decline(string token, CancellationToken ct = default) =>
        await Close(await Held(token, ct), InvitationOutcome.Declined, ct);

    public async Task<InvitationClosedResponse> Withdraw(Guid groupId, Guid invitationId,
        CancellationToken ct = default)
    {
        await MemberGroup(groupId, ct);

        var invitation = await context.Set<GroupInvitation>()
                             .FirstOrDefaultAsync(candidate =>
                                 candidate.Id == invitationId && candidate.GroupId == groupId, ct)
                         ?? throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                             "That invitation was not found.");

        return await Close(invitation, InvitationOutcome.Withdrawn, ct);
    }

    /// <summary>
    /// The half declining and withdrawing have in common: the invitation stops being
    /// pending, and whatever it was holding stops belonging to a stand-in nobody will ever
    /// sign in as.
    /// </summary>
    /// <remarks>
    /// One path for both, because the event is the same event -- the group is not getting
    /// this member -- and what happens to the money cannot depend on which side said so.
    /// What it must not do is quietly vanish: a group's balances sum to zero because every
    /// share belongs to somebody in the listing, and dropping a participant who held shares
    /// would leave the column not adding up with nothing to explain why. So the position
    /// moves, in full, to one named member, and the answer says whose it now is.
    /// </remarks>
    private async Task<InvitationClosedResponse> Close(GroupInvitation invitation,
        InvitationOutcome outcome, CancellationToken ct)
    {
        var groupName = await context.Set<Group>()
            .Where(candidate => candidate.Id == invitation.GroupId)
            .Select(candidate => candidate.Name)
            .FirstAsync(ct);

        var absorber = await participants.Absorber(invitation, ct);

        var moved = absorber is null
            ? ParticipantHandover.Nothing
            : await participants.HandOver(invitation.GroupId, invitation.ParticipantUserId, absorber.Id, ct);

        var standIn = invitation.ParticipantUserId;

        context.Remove(invitation);

        // The stand-in goes too. Nothing points at it any more -- the invitation is gone and
        // its position has moved -- and a row named after somebody who never joined, in no
        // group and holding nothing, is only there to be found by a later query that has no
        // business finding it.
        var emptied = await context.Set<User>().FirstOrDefaultAsync(row => row.Id == standIn, ct);

        if (emptied is not null)
            context.Remove(emptied);

        await context.SaveChangesAsync(ct);

        return new InvitationClosedResponse(
            invitation.Id,
            invitation.GroupId,
            groupName,
            invitation.Name,
            outcome,
            moved.SharesMoved,
            moved.AmountOwed,
            moved.PaymentsMoved,
            moved.AmountPaid,
            moved.RulesAffected,
            // Named only when they actually took something on, so a routine "no thanks"
            // does not read as though money had changed hands.
            moved.MovedAnything ? absorber?.Id : null,
            moved.MovedAnything ? Describe(absorber) : null);
    }

    /// <summary>
    /// The invitation a token names.
    /// </summary>
    /// <remarks>
    /// Holding the token is the whole authorisation, which is what makes it worth guarding:
    /// there is no second check, because there is no account for the invitation to be
    /// addressed to. A token that names nothing is a 404 -- and so is a token that has
    /// already been claimed, because claiming deletes the row. That is the same answer a
    /// mistyped link gets, and deliberately: the alternative is telling whoever holds a
    /// forwarded link that it used to be good.
    /// </remarks>
    private async Task<GroupInvitation> Held(string token, CancellationToken ct)
    {
        var normalized = (token ?? string.Empty).Trim();

        if (normalized.Length == 0)
            throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                "That invitation link was not found.");

        return await context.Set<GroupInvitation>()
                   .FirstOrDefaultAsync(candidate => candidate.Token == normalized, ct)
               ?? throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                   "That invitation link was not found. It may already have been claimed.");
    }

    /// <summary>
    /// What to call somebody: their name, or the address when nobody has told us one.
    /// </summary>
    private static string? Describe(User? user) =>
        user is null ? null : People.Display(user);

    private async Task<Group> MemberGroup(Guid groupId, CancellationToken ct)
    {
        var userId = userContext.User.Id;

        return await context.Set<Group>()
                   .FirstOrDefaultAsync(group =>
                       group.Id == groupId && group.Users.Any(user => user.Id == userId), ct)
               ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
    }

    /// <summary>
    /// The wire shape, projected from whatever query is handed in.
    /// </summary>
    /// <remarks>
    /// Order the query <em>before</em> it gets here. Ordering the result of this instead
    /// asks the database to sort by a member of a constructor call it has no way to build,
    /// and the whole query fails to translate -- which is what
    /// <c>GET /invitations</c> did on its first run against Postgres. It ran perfectly on
    /// the in-memory provider the unit tests use, because that one evaluates anything it
    /// cannot translate rather than refusing.
    /// </remarks>
    private static IQueryable<GroupInvitationResponse> Project(IQueryable<GroupInvitation> invitations) =>
        from invitation in invitations
        select new GroupInvitationResponse(
            invitation.Id,
            invitation.GroupId,
            invitation.Group.Name,
            invitation.Name,
            invitation.Token,
            invitation.InvitedBy == null
                ? null
                : invitation.InvitedBy.FirstName + " " + invitation.InvitedBy.LastName,
            invitation.InvitedAt,
            invitation.ParticipantUserId);

    /// <summary>
    /// A URL-safe token from the cryptographic generator, never from <c>Guid.NewGuid</c>:
    /// this is the whole of the authorisation, so it has to be unguessable rather than
    /// merely unique.
    /// </summary>
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
}

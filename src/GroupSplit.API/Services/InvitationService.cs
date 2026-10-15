using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Services;

/// <summary>
/// A guest's link, from the side of whoever holds it: what it says, taking the guest over,
/// and saying it is not for them. Plus the routes from when guests were invitations.
/// </summary>
/// <remarks>
/// A guest's link is heavier than a join link and is treated so. That one lets somebody in
/// as themselves, claiming nothing; this one hands over a position in the group's ledger, so
/// it is single use -- claiming clears it -- and it says nothing about the money before it is
/// claimed.
/// <para>
/// The group's side -- adding guests, issuing and stopping links -- is
/// <see cref="IGuestService"/>. The group-side methods here are the routes that predate it,
/// kept for one release so a client built before guests still works: inviting is adding with
/// a link, the list is the guests with a link out, and withdrawing stops the link.
/// </para>
/// </remarks>
public interface IInvitationService
{
    /// <summary>
    /// Adds each named person as a guest and issues each a link. The route from before
    /// guests; <see cref="IGuestService.Add"/> without links is the default now.
    /// </summary>
    /// <returns>The group's guests that have a link out, after the write.</returns>
    Task<IReadOnlyList<GroupInvitationResponse>> Invite(Guid groupId, InviteToGroupRequest request,
        CancellationToken ct = default);

    /// <summary>The group's guests that have a link out, in the shape from before guests.</summary>
    Task<IReadOnlyList<GroupInvitationResponse>> ForGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// The links the caller has opened that still work, newest first.
    /// </summary>
    /// <remarks>
    /// The holder's own list: "every link I have opened", which is what <see cref="Describe"/>
    /// writes down. It carries the tokens, which is the point -- this is how somebody gets
    /// back to a link they no longer have the message for -- and nothing is disclosed by it,
    /// since holding the token is what put the row there.
    /// </remarks>
    Task<IReadOnlyList<GroupInvitationResponse>> Mine(CancellationToken ct = default);

    /// <summary>
    /// What the link says about itself, for the page somebody lands on after opening one.
    /// Says nothing about what is recorded against the guest; see
    /// <see cref="InvitationClaimResponse"/>.
    /// </summary>
    /// <remarks>
    /// Remembers that the caller has seen it, so <see cref="Mine"/> can offer it again. A
    /// read that writes, which is worth being uneasy about and is the right place all the
    /// same: opening the link is exactly the event being recorded.
    /// </remarks>
    Task<InvitationClaimResponse> Describe(string token, CancellationToken ct = default);

    /// <summary>
    /// Claims one: the caller joins the group and takes the guest's place.
    /// </summary>
    /// <remarks>
    /// The one moment a position changes hands. Everything recorded against the guest -- their
    /// shares, anything they were down as having paid for, their place in the rules -- moves
    /// onto the caller's own account, with no amount changed. The guest ends, and the link
    /// with it, so there is nothing left for a second holder to claim.
    /// </remarks>
    Task<InvitationClaimedResponse> Claim(string token, CancellationToken ct = default);

    /// <summary>
    /// "This isn't me", from whoever holds the link: the link stops working. The guest stays,
    /// with everything recorded for them, and a member can send a new one.
    /// </summary>
    Task<InvitationClosedResponse> Decline(string token, CancellationToken ct = default);

    /// <summary>
    /// Stops the link of the guest with that invitation id. The route from before guests;
    /// <see cref="IGuestService.RevokeLink"/> is the one keyed by user id.
    /// </summary>
    Task<InvitationClosedResponse> Withdraw(Guid groupId, Guid invitationId,
        CancellationToken ct = default);
}

public sealed class InvitationService(
    ICurrentUser userContext,
    AppDbContext context,
    IGroupJoiner joiner,
    IGroupParticipants participants,
    IGuestService guests,
    TimeProvider clock)
    : IInvitationService
{
    public async Task<IReadOnlyList<GroupInvitationResponse>> Invite(Guid groupId,
        InviteToGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await guests.Add(groupId, new AddGuestsRequest { Names = request.Names, IssueLinks = true }, ct);

        return await ForGroup(groupId, ct);
    }

    public async Task<IReadOnlyList<GroupInvitationResponse>> ForGroup(Guid groupId,
        CancellationToken ct = default)
    {
        var userId = userContext.User.Id;
        var issuedSince = GroupGuest.IssuedSince(clock.GetUtcNow());

        // Scoped to the caller's own groups, and that scoping is load-bearing: the rows carry
        // the links, and a link is a claim on a position in this group's ledger.
        //
        // Only links that still work. A client built before links expired reads every row
        // here as a link to copy; the guest routes are where an expired one is shown.
        //
        // Ordered before the projection, not after -- see the note on Project.
        return await Project(
                from guest in context.Set<GroupGuest>()
                where guest.GroupId == groupId &&
                      guest.Token != null &&
                      guest.LinkIssuedAt > issuedSince &&
                      guest.Group.Users.Any(user => user.Id == userId)
                orderby guest.Name, guest.AddedAt
                select guest)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GroupInvitationResponse>> Mine(CancellationToken ct = default)
    {
        var userId = userContext.User.Id;
        var issuedSince = GroupGuest.IssuedSince(clock.GetUtcNow());

        // A link that has ended is absent because ending it forgets who opened it
        // (GuestLinkExtensions.EndLink), and a guest that has ended is filtered out besides.
        // The token test is the belt to those braces: a row written in the moment between a
        // link being read and being ended must not offer a link that no longer works.
        //
        // A link that has expired has not ended -- nothing runs at the moment it lapses -- so
        // it is the date that keeps it out, until a member issues another and the rows go.
        return await Project(
                from opened in context.Set<InvitationOpened>()
                join guest in context.Set<GroupGuest>()
                    on opened.GuestId equals guest.Id
                where opened.UserId == userId && guest.Token != null &&
                      guest.LinkIssuedAt > issuedSince
                orderby guest.LinkIssuedAt descending
                select guest)
            .ToListAsync(ct);
    }

    public async Task<InvitationClaimResponse> Describe(string token, CancellationToken ct = default)
    {
        var guest = await Held(token, clock.GetUtcNow(), ct);

        var userId = userContext.User.Id;

        // Before it is remembered: somebody who left after this link was sent is not somebody
        // it is waiting on, and the page should say so before the button rather than after.
        await joiner.EnsureMayRejoin(guest.GroupId, userId, guest.LinkIssuedAt!.Value, ct);

        var alreadyAMember = await context.Set<GroupMembership>()
            .AnyAsync(membership => membership.GroupId == guest.GroupId &&
                                    membership.UserId == userId, ct);

        // A member of the group opening the link -- to check it works before sending it --
        // must not put it into their own list of links waiting on them.
        if (!alreadyAMember)
            await Remember(guest.Id, userId, ct);

        // Read again rather than projected from what Held found, and so able to come back
        // empty: somebody may have claimed the link in the moments since.
        return await context.Set<GroupGuest>()
                   .Where(candidate => candidate.Id == guest.Id && candidate.Token == guest.Token)
                   .Select(candidate => new InvitationClaimResponse(
                       candidate.Id,
                       candidate.GroupId,
                       candidate.Group.Name,
                       candidate.Group.Users.Count,
                       candidate.Name,
                       candidate.LinkIssuedBy != null
                           ? candidate.LinkIssuedBy.FirstName + " " + candidate.LinkIssuedBy.LastName
                           : candidate.AddedBy != null
                               ? candidate.AddedBy.FirstName + " " + candidate.AddedBy.LastName
                               : null,
                       candidate.LinkIssuedAt!.Value,
                       alreadyAMember,
                       candidate.LinkIssuedAt!.Value + GroupGuest.LinkLifetime))
                   .FirstOrDefaultAsync(ct)
               ?? throw AlreadyClaimed();
    }

    public Task<InvitationClaimedResponse> Claim(string token, CancellationToken ct = default) =>
        AgainIfARuleClashed(() => ClaimOnce(token, ct));

    private async Task<InvitationClaimedResponse> ClaimOnce(string token, CancellationToken ct)
    {
        var user = userContext.User;

        // The claim is judged at the moment it arrives, once. Somebody who pressed the button
        // while the link worked gets the whole of the claim even if the transaction below
        // finishes a moment after the link's expiry -- and a claim that arrives at or after
        // it gets the 404, before anything is written. Reading the clock again part way
        // through would let a claim pass the first check and fail the second: a refusal
        // after the work had begun, and on a provider without transactions a half-claim.
        var now = clock.GetUtcNow();
        var held = await Held(token, now, ct);

        var standIn = held.ParticipantUserId;
        var name = held.Name;
        var issuedAt = held.LinkIssuedAt!.Value;

        // Before anything is written, so a refusal leaves the link standing for the person it
        // was meant for.
        await joiner.EnsureMayRejoin(held.GroupId, user.Id, issuedAt, ct);

        Group group = null!;

        // One transaction for the whole claim, run by the context's execution strategy: the
        // deployed provider retries transient failures, and a transaction the strategy did
        // not open is one it refuses to run at all.
        var retrying = false;

        var taken = await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A retry from what the database holds; see CallerScope.StartOver.
            if (retrying)
                context.StartOver(user);

            retrying = true;

            var guest = await Held(token, now, ct);

            group = await context.Set<Group>()
                .Include(candidate => candidate.Users)
                .FirstAsync(candidate => candidate.Id == guest.GroupId, ct);

            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // The guest ends first, and the link with it. The token is a concurrency token, so
            // this is what makes the link work once: two people claiming the same link at the
            // same moment both get past Held, but only one update finds the token still there.
            // On Postgres the second waits on the first one's lock, then finds the row no
            // longer matches and updates nothing, which EF reports as a concurrency failure --
            // and at that point the loser has written nothing, so there is nothing to undo and
            // no membership to take back. The same holds against a member replacing or
            // stopping the link, or removing the guest, at that moment.
            guest.EndedAt = now;
            guest.EndReason = GuestEndReason.Claimed;
            guest.EndedByUserId = user.Id;

            await context.EndLink(guest, ct);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw AlreadyClaimed();
            }

            // The position moves onto their own account: their shares become the claimer's,
            // the expenses the guest was down as having paid for become theirs, and their
            // places in the rules follow them. No amount changes, so every transaction still
            // divides into exactly its own amount.
            //
            // Before the join, which needs no membership to have happened yet: the position
            // moves, and then the person walks in and finds it theirs.
            var handover = await participants.HandOver(group.Id, standIn, user.Id, ct);

            // Written before the join, which finds the member's own rule by asking the
            // database whose it is: the rule the group held for the guest is theirs now,
            // and the join must see that rather than provision a second.
            await context.SaveChangesAsync(ct);

            // Join's own saves run inside this transaction. EF puts a savepoint before every
            // save made inside one and rolls back to it if the save fails, which is what keeps
            // the recovery in IMemberSplitRules.EnsureFor working here.
            await joiner.Join(group, user, issuedAt, ct);

            // The stand-in stays. It holds nothing now, so it reads as nobody to the group,
            // and it is what the membership history names for the time before the claim.
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return handover;
        });

        return new InvitationClaimedResponse(
            group.Id,
            group.Name,
            group.Users.Count,
            name,
            taken.SharesMoved,
            taken.AmountOwed,
            taken.PaymentsMoved,
            taken.AmountPaid,
            taken.RulesAffected,
            taken.SettlementsCancelled,
            taken.AmountCancelled);
    }

    public async Task<InvitationClosedResponse> Decline(string token, CancellationToken ct = default) =>
        await StopLink(await Held(token, clock.GetUtcNow(), ct), InvitationOutcome.Declined, ct);

    public async Task<InvitationClosedResponse> Withdraw(Guid groupId, Guid invitationId,
        CancellationToken ct = default)
    {
        var userId = userContext.User.Id;

        var guest = await context.Set<GroupGuest>()
                        .FirstOrDefaultAsync(candidate =>
                            candidate.Id == invitationId && candidate.GroupId == groupId &&
                            candidate.Group.Users.Any(user => user.Id == userId), ct)
                    ?? throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                        "That invitation was not found.");

        return await StopLink(guest, InvitationOutcome.Withdrawn, ct);
    }

    /// <summary>
    /// The link stops working; the guest stays, holding everything they held.
    /// </summary>
    /// <remarks>
    /// Turning a link down used to drop the guest and hand everything they held to some
    /// member, because a guest could not be settled with or removed and so had to go
    /// somewhere. Neither is true now. Whoever opened the link is saying it is not them,
    /// which is a fact about the link: the guest is still the person the group spent with.
    /// </remarks>
    private async Task<InvitationClosedResponse> StopLink(GroupGuest guest, InvitationOutcome outcome,
        CancellationToken ct)
    {
        var groupName = await context.Set<Group>()
            .Where(candidate => candidate.Id == guest.GroupId)
            .Select(candidate => candidate.Name)
            .FirstAsync(ct);

        if (guest.Token is not null)
        {
            await context.EndLink(guest, ct);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw AlreadyClaimed();
            }
        }

        return new InvitationClosedResponse(
            guest.Id, guest.GroupId, groupName, guest.Name, outcome,
            0, 0m, 0, 0m, 0, 0, null, null);
    }

    /// <summary>
    /// Runs a claim again, once, if it lost a race for one of a group's rules.
    /// </summary>
    /// <remarks>
    /// A claim hands the guest's "all for one" rule to the claimer, renaming it for them or
    /// retiring it beside theirs under the next free name. Two claims at the same moment --
    /// two guests merged into the same member, say -- each work out the same free name, and
    /// the second save breaks the name index. Nothing of that attempt was kept: the claim's
    /// transaction rolled back. So it goes again from what the database now holds, where the
    /// first one's rule is visible and is picked around. Once, not until it works: a second
    /// clash means something other than a race, and that should be seen.
    /// </remarks>
    private async Task<T> AgainIfARuleClashed<T>(Func<Task<T>> once)
    {
        try
        {
            return await once();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
                  {
                      SqlState: PostgresErrorCodes.UniqueViolation,
                      ConstraintName: "IX_SplitRule_GroupId_Name"
                  })
        {
            context.StartOver(userContext.User);

            return await once();
        }
    }

    /// <summary>
    /// Writes down that this account has opened this guest's link, so it can be offered again.
    /// </summary>
    /// <remarks>
    /// Once. Re-reading a link does not make it newer, and the list is ordered by when the
    /// link was issued rather than by when somebody last looked.
    /// </remarks>
    private async Task Remember(Guid guestId, Guid userId, CancellationToken ct)
    {
        var already = await context.Set<InvitationOpened>()
            .AnyAsync(opened => opened.GuestId == guestId && opened.UserId == userId, ct);

        if (already)
            return;

        var row = context.Add(new InvitationOpened
        {
            GuestId = guestId,
            UserId = userId,
            OpenedAt = clock.GetUtcNow()
        });

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Somebody else's request wrote the same row between the check above and this,
            // which is the answer this method wanted anyway: a double-clicked button, a page
            // rendered on the server and again after hydration. The failed insert is detached
            // rather than the tracker cleared, because this request goes on to read.
            row.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The guest a token names.
    /// </summary>
    /// <remarks>
    /// Holding the token is the whole authorisation, which is what makes it worth guarding:
    /// there is no second check, because there is no account for the link to be addressed
    /// to. A token that names nothing is a 404 -- and so is one that has been claimed,
    /// replaced or stopped, or that has expired. That is the same answer a mistyped link
    /// gets, and deliberately: the alternative is telling whoever holds a forwarded link
    /// that it used to be good. A join link says it expired, because it hands over nothing;
    /// this one would be confirming to a stranger that a position was once on offer.
    /// <para>
    /// Expiry is checked before anything else about the caller, the rejoin rule included,
    /// so an expired link answers the same 404 to everybody rather than telling somebody
    /// who left the group that it was issued before they went.
    /// </para>
    /// </remarks>
    private async Task<GroupGuest> Held(string token, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = (token ?? string.Empty).Trim();

        if (normalized.Length == 0)
            throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                "That invitation link was not found.");

        var guest = await context.Set<GroupGuest>()
                        .FirstOrDefaultAsync(candidate => candidate.Token == normalized, ct)
                    ?? throw AlreadyClaimed();

        return guest.HasWorkingLink(now) ? guest : throw AlreadyClaimed();
    }

    /// <summary>
    /// What a link answers once it has stopped working, whenever in the request that is
    /// found out: before reading it, or while claiming it.
    /// </summary>
    private static NotFoundException AlreadyClaimed() =>
        new(ErrorCodes.GroupInvitationNotFound,
            "That invitation link was not found. It may already have been claimed, been replaced, or expired.");

    /// <summary>
    /// The wire shape from before guests, projected from whatever query is handed in. Only
    /// for guests with a link out.
    /// </summary>
    /// <remarks>
    /// Order the query <em>before</em> it gets here. Ordering the result of this instead
    /// asks the database to sort by a member of a constructor call it has no way to build,
    /// and the whole query fails to translate -- which the in-memory provider the unit tests
    /// use does not notice, because it evaluates anything it cannot translate.
    /// </remarks>
    private static IQueryable<GroupInvitationResponse> Project(IQueryable<GroupGuest> linked) =>
        from guest in linked
        select new GroupInvitationResponse(
            guest.Id,
            guest.GroupId,
            guest.Group.Name,
            guest.Name,
            guest.Token!,
            guest.LinkIssuedBy != null
                ? guest.LinkIssuedBy.FirstName + " " + guest.LinkIssuedBy.LastName
                : guest.AddedBy != null
                    ? guest.AddedBy.FirstName + " " + guest.AddedBy.LastName
                    : null,
            guest.LinkIssuedAt!.Value,
            guest.ParticipantUserId,
            guest.LinkIssuedAt!.Value + GroupGuest.LinkLifetime);
}

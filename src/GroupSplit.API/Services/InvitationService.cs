using System.Buffers.Text;
using System.Security.Cryptography;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    /// The invitations the caller has opened and not yet answered, newest first.
    /// </summary>
    /// <remarks>
    /// The invitee's own list. It used to be "every invitation sent to my address", and
    /// there are no addresses any more -- so it is "every invitation whose link I have
    /// opened" instead, which is what <see cref="Describe"/> writes down.
    /// <para>
    /// It carries the tokens, which is the point: this is the list somebody uses to get
    /// back to a link they have already opened once and no longer have the message for.
    /// Nothing is disclosed by it, since holding the token is what put the row there.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<GroupInvitationResponse>> Mine(CancellationToken ct = default);

    /// <summary>
    /// What the link says about itself, for the page somebody lands on after opening one.
    /// Says nothing about what is recorded against the person; see
    /// <see cref="InvitationClaimResponse"/>.
    /// </summary>
    /// <remarks>
    /// Remembers that the caller has seen it, so <see cref="Mine"/> can offer it again. A
    /// read that writes, which is worth being uneasy about and is the right place all the
    /// same: opening the link is exactly the event being recorded, and the alternative is a
    /// second round trip from the one page that already knows.
    /// </remarks>
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
    IGroupParticipants participants,
    IMemberSplitRules memberRules)
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

        // Before anything else, so a caller outside the group is told so whatever they sent.
        await MemberGroup(groupId, ct);

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

        // Every name or none. Each one's rule is saved before the next is named, so the batch
        // is several saves, and a failure part way through must not leave the first few
        // invited and the request answered with an error. Run by the execution strategy, as
        // a transaction has to be on the deployed provider; see Fresh for why each attempt
        // starts from nothing.
        var retrying = false;

        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (retrying)
                Fresh(inviter);

            retrying = true;

            var group = await MemberGroup(groupId, ct);

            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            foreach (var name in names)
            {
                // The stand-in first, because the invitation is not merely a note that
                // somebody was asked: it is what makes this person somebody the group can
                // give a share to, which needs a row to give it to.
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

                // And the rule every participant has: all of it is for them. The expense
                // somebody was invited for is often exactly that one -- their flight, their
                // ticket -- and it should not have to wait for them to join. EnsureFor saves,
                // so each name's rule is written before the next is named, and two invitees
                // called Dani get "All for Dani" and "All for Dani (2)" rather than a
                // collision.
                await memberRules.EnsureFor(group, participant, ct);
            }

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

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

    public async Task<IReadOnlyList<GroupInvitationResponse>> Mine(CancellationToken ct = default)
    {
        var userId = userContext.User.Id;

        // Answered invitations are absent because the row is gone, not because this filters
        // them out: opening one is remembered against the invitation, and answering it
        // deletes the invitation and cascades. So there is no state here that could drift
        // out of step with whether the group is still waiting.
        //
        // Ordered before the projection, not after -- see the note on Project.
        return await Project(
                from opened in context.Set<InvitationOpened>()
                join invitation in context.Set<GroupInvitation>()
                    on opened.InvitationId equals invitation.Id
                where opened.UserId == userId
                orderby invitation.InvitedAt descending
                select invitation)
            .ToListAsync(ct);
    }

    public async Task<InvitationClaimResponse> Describe(string token, CancellationToken ct = default)
    {
        var invitation = await Held(token, ct);

        var userId = userContext.User.Id;

        // Before it is remembered: somebody who left after this was sent is not somebody it
        // is waiting on, and the page should say so before the button rather than after.
        await joiner.EnsureMayRejoin(invitation.GroupId, userId, invitation.InvitedAt, ct);

        // Asked once and used twice: to answer the page, and to decide whether this is
        // worth remembering.
        var alreadyAMember = await context.Set<GroupMembership>()
            .AnyAsync(membership => membership.GroupId == invitation.GroupId &&
                                    membership.UserId == userId, ct);

        // A member of the group is not somebody this invitation is waiting on. They open the
        // link to check it works before sending it, which is a sensible thing to do and must
        // not put an invitation meant for somebody else into their own "waiting on you" --
        // a list of decisions they cannot make, and could only clear by answering on the
        // invitee's behalf.
        if (!alreadyAMember)
            await Remember(invitation.Id, userId, ct);

        // Read again rather than projected from what Held found, and so able to come back
        // empty: somebody may have claimed the link in the moments since.
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
                       alreadyAMember))
                   .FirstOrDefaultAsync(ct)
               ?? throw AlreadyClaimed();
    }

    public async Task<InvitationClaimedResponse> Claim(string token, CancellationToken ct = default)
    {
        var user = userContext.User;
        var held = await Held(token, ct);

        var standIn = held.ParticipantUserId;
        var name = held.Name;
        var invitedAt = held.InvitedAt;

        // Before anything is written, so a refusal leaves the invitation standing for the
        // person it was meant for.
        await joiner.EnsureMayRejoin(held.GroupId, user.Id, invitedAt, ct);

        Group group = null!;

        // One transaction for the whole claim, run by the context's execution strategy: the
        // deployed provider retries transient failures, and a transaction the strategy did
        // not open is one it refuses to run at all.
        var retrying = false;

        var taken = await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A retry from what the database holds. See Fresh: a retry on top of the last
            // attempt's tracked writes skips some, repeats others, and on a claim can end by
            // deleting a stand-in whose shares were never moved.
            if (retrying)
                Fresh(user);

            retrying = true;

            var invitation = await Held(token, ct);

            group = await context.Set<Group>()
                .Include(candidate => candidate.Users)
                .FirstAsync(candidate => candidate.Id == invitation.GroupId, ct);

            // The invitation is the first thing in it. The row is the single-use token, so
            // taking it first is what makes the link work once: two people claiming the same
            // link at the same moment both get past Held, but only one of them can delete the
            // row. On Postgres the second delete waits on the first one's lock and then finds
            // nothing, which EF reports as a concurrency failure -- and at that point the
            // loser has written nothing, so there is nothing to undo and no membership to take
            // back. Everything after this is the winner's alone.
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            context.Remove(invitation);

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw AlreadyClaimed();
            }

            // The position moves onto their own account: their shares become the claimer's,
            // and the expenses the stand-in was down as having paid for become theirs. No
            // amount changes, so every transaction still divides into exactly its own amount.
            // Transfer, not prune. The group wrote "Carlos gets one share" and meant it, so
            // Carlos claiming his link is the group getting what it asked for -- and pruning
            // here left the new member out of the very templates that named them, silently.
            //
            // Before the join, which needs no membership to have happened yet: the position
            // moves, and then the person walks in and finds it theirs.
            var handover = await participants.HandOver(
                group.Id, standIn, user.Id, RuleHandling.Transfer, ct);

            // Written before the join, which finds the member's own rule by asking the
            // database whose it is: the rule the group held for the stand-in is theirs now,
            // and the join must see that rather than provision a second.
            await context.SaveChangesAsync(ct);

            // Join's own saves run inside this transaction. EF puts a savepoint before every
            // save made inside one and rolls back to it if the save fails, which is what keeps
            // the recovery in IMemberSplitRules.EnsureFor working here: without it, the unique
            // violation it catches would leave Postgres refusing every statement after it.
            await joiner.Join(group, user, invitedAt, ct);

            // The stand-in with it, now that nothing points at it. It exists to hold a
            // position until somebody claims it, and this is that moment; leaving it would
            // leave a row named after a person who is now in the group under their own
            // account, turning up in nothing and explaining nothing.
            await DiscardStandInIfUnused(standIn, ct);

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
            taken.RulesAffected);
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
            : await participants.HandOver(invitation.GroupId, invitation.ParticipantUserId,
                absorber.Id, RuleHandling.Prune, ct);

        var standIn = invitation.ParticipantUserId;

        context.Remove(invitation);

        // The stand-in goes too, but only once its position has actually moved and no
        // historical split-rule version still names it. The first condition is not tidiness:
        // TransactionSplit.UserId and Transaction.UserId are required, so their foreign keys
        // cascade, and deleting a stand-in that still holds shares would take those rows with
        // it -- silently. The second protects the immutable rule history: the newly opened
        // version may omit the stand-in while the superseded version must retain it.
        //
        // So a hand-over having run is the precondition. It moves everything a stand-in can
        // hold, because a stand-in can only ever be named inside its own group: every path
        // that writes a payer or a share checks it against that group's participants, and
        // there is nobody who could name one on a personal expense. The helper below then
        // decides whether deleting the now-empty account is safe for rule history as well.
        if (absorber is not null)
            await DiscardStandInIfUnused(standIn, ct);

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
            moved.RulesEmptied,
            // Named only when they actually took something on, so a routine "no thanks"
            // does not read as though money had changed hands.
            moved.MovedAnything ? absorber?.Id : null,
            moved.MovedAnything ? Describe(absorber) : null);
    }

    /// <summary>
    /// Starts an attempt at a multi-save transaction from the database rather than from
    /// whatever the last attempt left tracked.
    /// </summary>
    /// <remarks>
    /// The deployed provider retries a transaction that hit a transient failure by running
    /// the whole delegate again on the same context. Every save inside the first attempt
    /// marked its changes as stored, and the rollback did not unmark them, so the second
    /// attempt would read tracked rows that claim writes the database no longer has: an
    /// update to a value already set is skipped, an addition to an amount is made twice, and
    /// a membership the first attempt reopened reads as already open. Clearing first makes an
    /// attempt mean the same thing whichever one it is. The signed-in account is put back,
    /// because the services hold on to it and it is the one row every write here refers to.
    /// <para>
    /// Only before a retry. The first attempt has no earlier one to forget, and clearing then
    /// would throw away anything this request had tracked and not yet saved before the
    /// claim or invitation began -- changes that are nobody's to discard here.
    /// </para>
    /// </remarks>
    private void Fresh(User signedIn)
    {
        context.ChangeTracker.Clear();

        // That one row, and nothing reachable from it: whatever its navigations held belongs
        // to the attempt being thrown away.
        context.Entry(signedIn).State = EntityState.Unchanged;
    }

    /// <summary>
    /// Deletes a stand-in after its invitation is answered, unless rule history still needs
    /// it as the principal for a superseded weighted version.
    /// </summary>
    /// <remarks>
    /// A hand-over opens a new version without the stand-in, but it deliberately leaves the
    /// closed version unchanged. Deleting the account in that case would let the required
    /// foreign key cascade through the old participant rows and erase the rule's history.
    /// The same protection is needed for claiming as for declining or withdrawing.
    /// </remarks>
    private async Task DiscardStandInIfUnused(Guid standIn, CancellationToken ct)
    {
        if (await context.Set<SplitRuleParticipant>()
                .AnyAsync(participant => participant.UserId == standIn, ct))
            return;

        // The hand-over repoints or retires the rule the group held for them, so nothing
        // should name them here either. Asked all the same, of the tracker as well as the
        // database: a version still naming them is a restricted key the delete cannot pass.
        // Declining and withdrawing have not saved yet, so a version the hand-over moved or
        // deleted still names them in the database; the tracker is what says it no longer will.
        var tracked = context.ChangeTracker.Entries<SoleSplitRuleVersion>().ToList();

        if (tracked.Any(entry => entry.State != EntityState.Deleted && entry.Entity.UserId == standIn))
            return;

        var settled = tracked
            .Where(entry => entry.State == EntityState.Deleted || entry.Entity.UserId != standIn)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        var stored = await context.Set<SoleSplitRuleVersion>()
            .AsNoTracking()
            .Where(version => version.UserId == standIn)
            .Select(version => version.Id)
            .ToListAsync(ct);

        if (stored.Any(id => !settled.Contains(id)))
            return;

        var emptied = await context.Set<User>()
            .FirstOrDefaultAsync(row => row.Id == standIn, ct);

        if (emptied is not null)
            context.Remove(emptied);
    }

    /// <summary>
    /// Writes down that this account has seen this invitation, so it can be offered again.
    /// </summary>
    /// <remarks>
    /// Once. Re-reading a link does not make the invitation newer, and the list is ordered
    /// by when the group sent it rather than by when somebody last looked.
    /// <para>
    /// A stand-in is never here: nobody can sign in as one, so the caller is always a real
    /// account. What can happen is a member of the group opening the link -- to check it
    /// works, or because they were sent their own -- and there is no harm in that being
    /// remembered; the claim page tells them they are already in.
    /// </para>
    /// </remarks>
    private async Task Remember(Guid invitationId, Guid userId, CancellationToken ct)
    {
        var already = await context.Set<InvitationOpened>()
            .AnyAsync(opened => opened.InvitationId == invitationId && opened.UserId == userId, ct);

        if (already)
            return;

        var row = context.Add(new InvitationOpened
        {
            InvitationId = invitationId,
            UserId = userId,
            OpenedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Somebody else's request wrote the same row between the check above and this,
            // which is the answer this method wanted anyway. Two overlapping reads of one
            // link are ordinary -- a double-clicked button, a page rendered on the server
            // and again after hydration -- and neither of them should turn a read into a
            // 500. The same race UserProvisioner catches on a first sign-in.
            //
            // The failed insert is detached rather than the tracker cleared: this runs
            // inside a request that goes on to read, and clearing would take its entities
            // with it.
            row.State = EntityState.Detached;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // The invitation went between Held and this insert: somebody claimed it while
            // this person's page was loading. That is the answer a link already claimed
            // gets, not a 500 -- the same race Claim settles, met on the read instead.
            row.State = EntityState.Detached;

            throw AlreadyClaimed();
        }
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
               ?? throw AlreadyClaimed();
    }

    /// <summary>
    /// What a link answers once its invitation is gone, whenever in the request that is
    /// found out: before reading it, while remembering it was opened, or while claiming it.
    /// </summary>
    private static NotFoundException AlreadyClaimed() =>
        new(ErrorCodes.GroupInvitationNotFound,
            "That invitation link was not found. It may already have been claimed.");

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

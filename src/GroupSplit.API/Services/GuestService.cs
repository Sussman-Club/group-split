using System.Buffers.Text;
using System.Security.Cryptography;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// The group's side of its guests: adding them, and the link a member can send so the real
/// person can take their place.
/// </summary>
/// <remarks>
/// Adding somebody used to invite them: a link was minted at once, and until it was answered
/// the person was "pending" -- choosable for money but not settleable, and impossible to drop
/// without handing everything they held to some member. Most of the people a group splits
/// with never sign up, so a guest is the default now: a name the members record everything
/// for, settleable like anybody else, and removable once square. A link is something a
/// member issues when the real person wants in, and can replace or stop at any time.
/// <para>
/// Guests are addressed by their stand-in's user id, the same id their shares, payments and
/// repayments name -- so there is one id per person, and nothing to translate between.
/// </para>
/// </remarks>
public interface IGuestService
{
    /// <summary>
    /// Adds each named person to a group the caller belongs to, as a guest.
    /// </summary>
    /// <returns>The guests just added, in the order they were named.</returns>
    Task<IReadOnlyList<GroupGuestResponse>> Add(Guid groupId, AddGuestsRequest request,
        CancellationToken ct = default);

    /// <summary>The group's current guests, links included. Readable by its members.</summary>
    Task<IReadOnlyList<GroupGuestResponse>> ForGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// The guest's link: the one already out, or a new one when there is none or
    /// <paramref name="replace"/> asks for it.
    /// </summary>
    Task<GroupGuestResponse> IssueLink(Guid groupId, Guid userId, bool replace,
        CancellationToken ct = default);

    /// <summary>Stops the guest's link, if there is one. The guest stays.</summary>
    Task RevokeLink(Guid groupId, Guid userId, CancellationToken ct = default);
}

public sealed class GuestService(
    ICurrentUser userContext,
    AppDbContext context,
    IGroupParticipants participants,
    IMemberSplitRules memberRules)
    : IGuestService
{
    /// <summary>
    /// Bytes of randomness behind a token, before encoding, matching the join link's. This
    /// one guards more -- a position in a ledger rather than a seat in a group -- so it is
    /// certainly not less.
    /// </summary>
    private const int TokenBytes = 24;

    public async Task<IReadOnlyList<GroupGuestResponse>> Add(Guid groupId, AddGuestsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var caller = userContext.User;

        // Before anything else, so a caller outside the group is told so whatever they sent.
        await MemberGroup(groupId, ct);

        var names = request.Names
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => name.Length > 0)
            .ToList();

        // Nothing usable in the list at all. Refused rather than answered with the group's
        // existing guests, which would read as success for a request that asked for
        // something and got nothing.
        if (names.Count == 0)
            throw new ValidationException(ErrorCodes.GroupInvitationNoName,
                "A guest needs a name.");

        var addedAt = DateTimeOffset.UtcNow;
        var added = new List<Guid>();

        // Every name or none. Each one's rule is saved before the next is named, so the batch
        // is several saves, and a failure part way through must not leave the first few added
        // and the request answered with an error. Run by the execution strategy, as a
        // transaction has to be on the deployed provider.
        var retrying = false;

        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // A retry from what the database holds rather than from what the failed attempt
            // left tracked; see CallerScope.StartOver.
            if (retrying)
            {
                context.StartOver(caller);
                added.Clear();
            }

            retrying = true;

            var group = await MemberGroup(groupId, ct);

            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            foreach (var name in names)
            {
                // The stand-in first: a guest is somebody the group can give a share to, which
                // needs a row to give it to. One per guest, and never an existing account --
                // there is no address to recognise one by.
                var participant = participants.StandInFor(name);

                context.Add(new GroupGuest
                {
                    Group = group,
                    Name = name,
                    Participant = participant,
                    AddedBy = caller,
                    AddedAt = addedAt,
                    Token = request.IssueLinks ? NewToken() : null,
                    LinkIssuedAt = request.IssueLinks ? addedAt : null,
                    LinkIssuedBy = request.IssueLinks ? caller : null
                });

                // And the rule every participant has: all of it is for them. The expense
                // somebody is added for is often exactly that one -- their flight, their
                // ticket. EnsureFor saves, so each name's rule is written before the next is
                // named, and two guests called Dani get "All for Dani" and "All for Dani (2)"
                // rather than a collision.
                await memberRules.EnsureFor(group, participant, ct);

                added.Add(participant.Id);
            }

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

        var rows = await Project(
                context.Set<GroupGuest>()
                    .Where(guest => guest.GroupId == groupId && added.Contains(guest.ParticipantUserId)))
            .ToListAsync(ct);

        return rows.OrderBy(row => added.IndexOf(row.UserId)).ToList();
    }

    public async Task<IReadOnlyList<GroupGuestResponse>> ForGroup(Guid groupId, CancellationToken ct = default)
    {
        await MemberGroup(groupId, ct);

        // Ordered before the projection, not after: see InvitationService.Project.
        return await Project(
                from guest in context.Set<GroupGuest>()
                where guest.GroupId == groupId
                orderby guest.Name, guest.AddedAt
                select guest)
            .ToListAsync(ct);
    }

    public async Task<GroupGuestResponse> IssueLink(Guid groupId, Guid userId, bool replace,
        CancellationToken ct = default)
    {
        var guest = await Guest(groupId, userId, ct);

        if (guest.Token is not null && !replace)
            return await Read(guest.Id, ct);

        // Replacing ends the old link properly -- the people who opened it are forgotten too,
        // so they are not handed this one -- and then issues the new.
        if (guest.Token is not null)
            await context.EndLink(guest, ct);

        guest.Token = NewToken();
        guest.LinkIssuedAt = DateTimeOffset.UtcNow;
        guest.LinkIssuedBy = userContext.User;

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Something else changed the guest first: another member issued a link, somebody
            // claimed the last one, or the guest was removed. Whatever the database now says
            // is the answer -- the link that won, or that there is no guest to issue one for.
            context.StartOver(userContext.User);

            var now = await Guest(groupId, userId, ct);

            if (now.Token is null)
                throw new ConflictException(ErrorCodes.GroupGuestChanged,
                    "Somebody else changed this guest at the same moment. Try again.");

            return await Read(now.Id, ct);
        }

        return await Read(guest.Id, ct);
    }

    public async Task RevokeLink(Guid groupId, Guid userId, CancellationToken ct = default)
    {
        var guest = await Guest(groupId, userId, ct);

        if (guest.Token is null)
            return;

        await context.EndLink(guest, ct);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Claimed, replaced or removed in the meantime. A replacement is a link somebody
            // just chose to send, and not this caller's to stop unseen; the other two leave
            // nothing to stop. Either way the answer is what is there now.
            throw new ConflictException(ErrorCodes.GroupGuestChanged,
                "Somebody else changed this guest at the same moment. Look again before stopping the link.");
        }
    }

    /// <summary>The current guest of a group the caller is in, by their user id.</summary>
    private async Task<GroupGuest> Guest(Guid groupId, Guid userId, CancellationToken ct)
    {
        await MemberGroup(groupId, ct);

        return await context.Set<GroupGuest>()
                   .FirstOrDefaultAsync(guest => guest.GroupId == groupId &&
                                                 guest.ParticipantUserId == userId, ct)
               ?? throw new NotFoundException(ErrorCodes.GroupGuestNotFound,
                   "That person is not a guest of this group.");
    }

    private Task<GroupGuestResponse> Read(Guid guestId, CancellationToken ct) =>
        Project(context.Set<GroupGuest>().Where(guest => guest.Id == guestId)).FirstAsync(ct);

    private Task<Group> MemberGroup(Guid groupId, CancellationToken ct) =>
        context.GroupOfMember(groupId, userContext.User.Id, ct);

    private static IQueryable<GroupGuestResponse> Project(IQueryable<GroupGuest> guests) =>
        from guest in guests
        select new GroupGuestResponse(
            guest.ParticipantUserId,
            guest.GroupId,
            guest.Group.Name,
            guest.Name,
            guest.AddedBy == null ? null : guest.AddedBy.FirstName + " " + guest.AddedBy.LastName,
            guest.AddedAt,
            guest.Token != null,
            guest.Token,
            guest.LinkIssuedBy == null ? null : guest.LinkIssuedBy.FirstName + " " + guest.LinkIssuedBy.LastName,
            guest.LinkIssuedAt);

    /// <summary>
    /// A URL-safe token from the cryptographic generator, never from <c>Guid.NewGuid</c>:
    /// this is the whole of the authorisation, so it has to be unguessable rather than
    /// merely unique.
    /// </summary>
    internal static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
}

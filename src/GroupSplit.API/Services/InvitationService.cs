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
/// Adding a member used to be a write the group made about somebody else: the address was
/// looked up, and if an account had it, that account was in the group from that moment --
/// and if none had, the address was silently dropped and the person who typed it was told
/// the member had been added. Both halves are wrong. Joining a group is something a person
/// agrees to, and an address with no account behind it is the ordinary case for a new
/// group, not an error to swallow.
/// </remarks>
public interface IInvitationService
{
    /// <summary>
    /// Invites each address to a group the caller belongs to. Addresses already in the
    /// group, or already invited, are skipped rather than refused: inviting five people of
    /// whom one is already there should not fail for the other four.
    /// </summary>
    /// <returns>The group's pending invitations after the write.</returns>
    Task<IReadOnlyList<GroupInvitationResponse>> Invite(Guid groupId, AddMemberRequest request,
        CancellationToken ct = default);

    /// <summary>Who a group is waiting on. Readable by its members.</summary>
    Task<IReadOnlyList<GroupInvitationResponse>> ForGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>The invitations addressed to the caller, whichever group sent them.</summary>
    Task<IReadOnlyList<GroupInvitationResponse>> Mine(CancellationToken ct = default);

    /// <summary>Takes the caller up on one, which is the only way to join a group.</summary>
    Task<Group> Accept(Guid invitationId, CancellationToken ct = default);

    /// <summary>Turns one down. The row goes; the group may ask again.</summary>
    Task Decline(Guid invitationId, CancellationToken ct = default);

    /// <summary>Withdraws one the caller's group sent.</summary>
    Task Withdraw(Guid groupId, Guid invitationId, CancellationToken ct = default);
}

public sealed class InvitationService(ICurrentUser userContext, AppDbContext context) : IInvitationService
{
    public async Task<IReadOnlyList<GroupInvitationResponse>> Invite(Guid groupId, AddMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inviter = userContext.User;
        var group = await MemberGroup(groupId, ct);

        var addresses = request.UserIdentifiers
            .Select(identifier => Normalize(identifier.Email))
            .Where(email => email.Length > 0)
            .Distinct()
            .ToList();

        // Both sets in one round trip each, because "already there" and "already asked" are
        // the two things that make an address a no-op and they are checked for every one.
        var members = await context.Set<Group>()
            .Where(candidate => candidate.Id == groupId)
            .SelectMany(candidate => candidate.Users)
            .Select(user => user.Email)
            .ToListAsync(ct);

        var alreadyMembers = members
            .Where(email => email is not null)
            .Select(email => Normalize(email!))
            .ToHashSet();

        var alreadyInvited = await context.Set<GroupInvitation>()
            .Where(invitation => invitation.GroupId == groupId)
            .Select(invitation => invitation.Email)
            .ToListAsync(ct);

        var pending = alreadyInvited.ToHashSet();

        var invitedAt = DateTimeOffset.UtcNow;

        foreach (var email in addresses)
        {
            if (alreadyMembers.Contains(email) || !pending.Add(email))
                continue;

            context.Add(new GroupInvitation
            {
                Group = group,
                Email = email,
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

        // Scoped to the caller's own groups: who a group is waiting on is a list of
        // addresses, and an address is a person.
        return await Describe(
                from invitation in context.Set<GroupInvitation>()
                where invitation.GroupId == groupId &&
                      invitation.Group.Users.Any(user => user.Id == userId)
                select invitation)
            .OrderBy(invitation => invitation.Email)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GroupInvitationResponse>> Mine(CancellationToken ct = default)
    {
        var email = Normalize(userContext.User.Email ?? string.Empty);

        // An account with no address on it has no way to be invited, and matching the empty
        // string would hand it every invitation that was ever stored badly.
        if (email.Length == 0)
            return [];

        return await Describe(
                context.Set<GroupInvitation>().Where(invitation => invitation.Email == email))
            .OrderByDescending(invitation => invitation.InvitedAt)
            .ToListAsync(ct);
    }

    public async Task<Group> Accept(Guid invitationId, CancellationToken ct = default)
    {
        var user = userContext.User;
        var invitation = await Addressed(invitationId, ct);

        var group = await context.Set<Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == invitation.GroupId, ct);

        // Accepting one for a group they are already in is a state the invitation should
        // not have been in; the row goes either way, so asking twice answers the same.
        if (group.Users.All(member => member.Id != user.Id))
        {
            group.Users.Add(user);
            await context.SaveChangesAsync(ct);

            // After the join, and separately: EF writes the join row itself, so the payload
            // on it is set on the row that now exists rather than on one built by hand.
            var membership = await context.Set<GroupMembership>()
                .FirstOrDefaultAsync(row => row.GroupId == group.Id && row.UserId == user.Id, ct);

            if (membership is not null)
                membership.JoinedAt = DateTimeOffset.UtcNow;
        }

        context.Remove(invitation);
        await context.SaveChangesAsync(ct);

        return group;
    }

    public async Task Decline(Guid invitationId, CancellationToken ct = default)
    {
        context.Remove(await Addressed(invitationId, ct));
        await context.SaveChangesAsync(ct);
    }

    public async Task Withdraw(Guid groupId, Guid invitationId, CancellationToken ct = default)
    {
        await MemberGroup(groupId, ct);

        var invitation = await context.Set<GroupInvitation>()
                             .FirstOrDefaultAsync(candidate =>
                                 candidate.Id == invitationId && candidate.GroupId == groupId, ct)
                         ?? throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                             "That invitation was not found.");

        context.Remove(invitation);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The invitation, checked to be addressed to the caller. A 404 for one that is not
    /// theirs would deny it exists; a 403 says what actually happened.
    /// </summary>
    private async Task<GroupInvitation> Addressed(Guid invitationId, CancellationToken ct)
    {
        var invitation = await context.Set<GroupInvitation>()
                             .FirstOrDefaultAsync(candidate => candidate.Id == invitationId, ct)
                         ?? throw new NotFoundException(ErrorCodes.GroupInvitationNotFound,
                             "That invitation was not found.");

        var email = Normalize(userContext.User.Email ?? string.Empty);

        if (email.Length == 0 || invitation.Email != email)
            throw new ForbiddenException(ErrorCodes.GroupInvitationNotYours,
                "That invitation was sent to somebody else.");

        return invitation;
    }

    private async Task<Group> MemberGroup(Guid groupId, CancellationToken ct)
    {
        var userId = userContext.User.Id;

        return await context.Set<Group>()
                   .FirstOrDefaultAsync(group =>
                       group.Id == groupId && group.Users.Any(user => user.Id == userId), ct)
               ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");
    }

    private static IQueryable<GroupInvitationResponse> Describe(IQueryable<GroupInvitation> invitations) =>
        from invitation in invitations
        select new GroupInvitationResponse(
            invitation.Id,
            invitation.GroupId,
            invitation.Group.Name,
            invitation.Email,
            invitation.InvitedBy == null
                ? null
                : invitation.InvitedBy.FirstName + " " + invitation.InvitedBy.LastName,
            invitation.InvitedAt);

    /// <summary>
    /// Addresses are stored and compared lower-cased. Case is not part of who somebody is,
    /// and the alternative is a database-collation question in the middle of a join.
    /// </summary>
    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}

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
/// The shareable way into a group: a member makes a link, pastes it where the group already
/// talks, and whoever holds it joins.
/// </summary>
/// <remarks>
/// An invitation reaches an address. A link reaches a thread, which is where a dinner group
/// actually adds somebody, and it works for a person who has no account yet -- the case
/// invitations exist for and the one an unsent email serves worst. The two live side by
/// side: this is a second way in, not a replacement.
/// <para>
/// One link at a time, per group. A pile of them is a list nobody can reason about, and
/// "revoke the link" has to mean something definite -- so making one puts out whatever was
/// standing, and revoking puts out everything that is. The read is a list all the same,
/// because two members pressing the button in the same second is a race no invariant here
/// forbids, and a list shows what is actually there rather than picking one and hiding the
/// other.
/// </para>
/// </remarks>
public interface IJoinLinkService
{
    /// <summary>
    /// The group's live links, newest first. Readable by its members, and empty for a group
    /// nobody has made one for -- which is an ordinary state and not a missing thing.
    /// </summary>
    Task<IReadOnlyList<GroupJoinLinkResponse>> ForGroup(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// Issues one, revoking whatever the group had. Any member may: sharing a group is not
    /// a privilege above being in it, and there is nothing else to be.
    /// </summary>
    Task<GroupJoinLinkResponse> Create(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// Puts out every live link into the group, so the URLs already shared stop working.
    /// </summary>
    Task Revoke(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// What the token says about itself, for the page somebody lands on before they join.
    /// Refuses an expired or revoked one by name rather than as a generic failure.
    /// </summary>
    Task<JoinLinkResponse> Describe(string token, CancellationToken ct = default);

    /// <summary>
    /// Joins the caller to the group the token names, and nothing else.
    /// </summary>
    /// <remarks>
    /// Following the same link twice is not an error. The answer says whether they were
    /// already in, and either way there is one membership at the end of it.
    /// </remarks>
    Task<JoinedGroupResponse> Accept(string token, CancellationToken ct = default);
}

public sealed class JoinLinkService(ICurrentUser userContext, AppDbContext context, IGroupJoiner joiner, TimeProvider clock)
    : IJoinLinkService
{
    /// <summary>
    /// Bytes of randomness behind a token, before encoding. Sixteen would already be beyond
    /// guessing; twenty-four costs eight more characters in a URL nobody types by hand and
    /// leaves no argument to have.
    /// </summary>
    private const int TokenBytes = 24;

    public async Task<IReadOnlyList<GroupJoinLinkResponse>> ForGroup(Guid groupId,
        CancellationToken ct = default)
    {
        await MemberGroup(groupId, ct);

        // Ordered before the projection, not after -- see the note on DescribeForGroup.
        return await DescribeForGroup(Live(groupId).OrderByDescending(link => link.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<GroupJoinLinkResponse> Create(Guid groupId, CancellationToken ct = default)
    {
        var group = await MemberGroup(groupId, ct);
        group.RefuseIfPersonalOf(userContext.User.Id);

        // Whatever was standing goes out with this one, and the database holds the group to
        // one unrevoked link. Two members pressing the button at the same moment both revoke
        // what they can see and both insert; the second insert breaks the index, and that
        // member goes round again -- revoking the first one's link this time -- so the newer
        // press wins and nothing is left half-alive.
        for (var attempt = 1; ; attempt++)
        {
            var now = clock.GetUtcNow();

            await RevokeStanding(groupId, now, ct);

            var link = new GroupJoinLink
            {
                Group = group,
                Token = NewToken(),
                CreatedBy = userContext.User,
                CreatedAt = now,
                ExpiresAt = now + GroupJoinLink.Lifetime
            };

            context.Add(link);

            try
            {
                await context.SaveChangesAsync(ct);

                return await DescribeForGroup(
                        context.Set<GroupJoinLink>().Where(candidate => candidate.Id == link.Id))
                    .FirstAsync(ct);
            }
            catch (DbUpdateException exception)
                when (attempt < 3 &&
                      exception.InnerException is PostgresException
                      {
                          SqlState: PostgresErrorCodes.UniqueViolation,
                          ConstraintName: "IX_GroupJoinLink_GroupId_Unrevoked"
                      })
            {
                context.Entry(link).State = EntityState.Detached;
            }
        }
    }

    public async Task Revoke(Guid groupId, CancellationToken ct = default)
    {
        await MemberGroup(groupId, ct);

        await RevokeStanding(groupId, clock.GetUtcNow(), ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task<JoinLinkResponse> Describe(string token, CancellationToken ct = default)
    {
        var link = await Usable(token, ct);
        var userId = userContext.User.Id;

        // Said on the landing page, before the button, rather than after it: a link somebody
        // held while they were in the group is not a way back for them.
        await joiner.EnsureMayRejoin(link.GroupId, userId, link.CreatedAt, ct);

        return await context.Set<GroupJoinLink>()
            .Where(candidate => candidate.Id == link.Id)
            .Select(candidate => new JoinLinkResponse(
                candidate.GroupId,
                candidate.Group.Name,
                candidate.Group.Users.Count,
                candidate.CreatedBy == null
                    ? null
                    : candidate.CreatedBy.FirstName + " " + candidate.CreatedBy.LastName,
                candidate.ExpiresAt,
                candidate.Group.Users.Any(member => member.Id == userId)))
            .FirstAsync(ct);
    }

    public async Task<JoinedGroupResponse> Accept(string token, CancellationToken ct = default)
    {
        var user = userContext.User;
        var link = await Usable(token, ct);

        var group = await context.Set<Group>()
            .Include(candidate => candidate.Users)
            .FirstAsync(candidate => candidate.Id == link.GroupId, ct);

        var outcome = await joiner.Join(group, user, link.CreatedAt, ct);

        // No standing invitation is answered by this, and none can be. This used to close
        // the one addressed to the joiner's email, and there are no addresses any more: an
        // invitation names a person the group made up and holds a stand-in of its own, which
        // only claiming that invitation's own link attaches an account to. Somebody who was
        // named *and* sent the group link comes in here as themselves, and the group is
        // still waiting on the person it named -- which it should be, since nothing has told
        // it the two are the same. Withdrawing the invitation is how it says so, and that
        // hands the position over rather than dropping it.
        return new JoinedGroupResponse(group.Id, group.Name, group.Users.Count,
            outcome is JoinOutcome.AlreadyAMember);
    }

    /// <summary>
    /// The link a token names, checked to be one somebody may still act on.
    /// </summary>
    /// <remarks>
    /// The three answers are kept apart on purpose. A link that ran out and a link that was
    /// withdrawn are different things to have happened to the person holding it, and both
    /// are different from a URL that was mistyped -- "that link has expired, ask for a new
    /// one" is actionable where a bare not-found is not.
    /// </remarks>
    private async Task<GroupJoinLink> Usable(string token, CancellationToken ct)
    {
        var normalized = (token ?? string.Empty).Trim();

        var link = await context.Set<GroupJoinLink>()
                       .FirstOrDefaultAsync(candidate => candidate.Token == normalized, ct)
                   ?? throw new NotFoundException(ErrorCodes.GroupJoinLinkNotFound,
                       "That join link was not found.");

        // Revoked before it lapsed. One closed at its own expiry -- see RevokeStanding -- is
        // an expired link, and says so below.
        if (link.RevokedAt is { } revokedAt && revokedAt < link.ExpiresAt)
            throw new ConflictException(ErrorCodes.GroupJoinLinkRevoked,
                "That join link has been withdrawn by the group.");

        if (link.ExpiresAt <= clock.GetUtcNow())
            throw new ConflictException(ErrorCodes.GroupJoinLinkExpired,
                "That join link has expired.");

        return link;
    }

    private IQueryable<GroupJoinLink> Live(Guid groupId)
    {
        var now = clock.GetUtcNow();

        return context.Set<GroupJoinLink>()
            .Where(link => link.GroupId == groupId && link.RevokedAt == null && link.ExpiresAt > now);
    }

    /// <summary>
    /// Closes every link the group has not revoked, expired ones included. Does not save:
    /// the callers either save on their own account or have a row of their own to write in
    /// the same round trip.
    /// </summary>
    /// <remarks>
    /// Expired ones too, because the database counts them: it allows one unrevoked link per
    /// group and cannot ask an index what time it is. One that had already lapsed is closed
    /// at the moment it lapsed rather than now, which is what keeps <see cref="Usable"/>
    /// telling whoever opens it that it expired rather than that the group withdrew it.
    /// </remarks>
    private async Task RevokeStanding(Guid groupId, DateTimeOffset now, CancellationToken ct)
    {
        var standing = await context.Set<GroupJoinLink>()
            .Where(link => link.GroupId == groupId && link.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var link in standing)
            link.RevokedAt = link.ExpiresAt < now ? link.ExpiresAt : now;
    }

    /// <summary>
    /// The wire shape a group's own members read, projected from whatever query is handed
    /// in.
    /// </summary>
    /// <remarks>
    /// Order the query <em>before</em> it gets here. Ordering the result of this instead
    /// asks the database to sort by a member of a constructor call it has no way to build,
    /// which is what made <c>GET /invitations</c> fail on its first run against Postgres
    /// while passing on the in-memory provider the unit tests use.
    /// </remarks>
    private static IQueryable<GroupJoinLinkResponse> DescribeForGroup(IQueryable<GroupJoinLink> links) =>
        links
            .Select(link => new GroupJoinLinkResponse(
                link.Id,
                link.GroupId,
                link.Group.Name,
                link.Token,
                link.CreatedBy == null
                    ? null
                    : link.CreatedBy.FirstName + " " + link.CreatedBy.LastName,
                link.CreatedAt,
                link.ExpiresAt));

    private Task<Group> MemberGroup(Guid groupId, CancellationToken ct) =>
        context.GroupOfMember(groupId, userContext.User.Id, ct);

    /// <summary>
    /// A URL-safe token from the cryptographic generator, never from <c>Guid.NewGuid</c>:
    /// this is the whole of the authorisation, so it has to be unguessable rather than
    /// merely unique.
    /// </summary>
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
}

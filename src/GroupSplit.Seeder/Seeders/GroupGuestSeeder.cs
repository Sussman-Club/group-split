using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Seeds the people groups share costs with who have no account: their guests.
/// </summary>
/// <remarks>
/// Without these, every surface that exists for guests is empty: a group's People tab shows
/// no Guests section, the home page's "Waiting on you" card never mentions a link, and
/// <c>groups guests</c> answers an empty array.
/// <para>
/// Three kinds on purpose, because they are different screens. Hannah, in Ski trip, is a
/// guest nobody has sent a link: the ordinary case, recorded for and settled with by the
/// members. Tomás, in Ski trip too, has a link out, so the Copy link / New link / Stop link
/// row has something to show. And one is in a group the demo account is <em>not</em> in,
/// whose link is the only way to reach the claim page -- that is what Book club is for in
/// <c>groups.json</c>.
/// </para>
/// <para>
/// One of them has no <c>AddedByUserId</c>. The column is nullable so that an account
/// deleting itself does not take the guests it added with it, and a nullable column nothing
/// exercises is a null-reference waiting for the first person who deletes an account -- so
/// the demo data covers it.
/// </para>
/// <para>
/// Before the expenses, and that ordering is the point rather than an accident. A guest is
/// somebody the group can give a share to, so a group with guests divides its spending
/// between more people than its membership -- and a demo database where the guests landed
/// last would have every balance computed as though they were not there.
/// </para>
/// </remarks>
[DependsOn(typeof(GroupSeeder))]
[DependsOn(typeof(UserSeeder))]
public class GroupGuestSeeder(
    AppDbContext db,
    TimeProvider clock,
    ILogger<GroupGuestSeeder> logger,
    ISeedDataSource<GuestSeedDto> source)
    : AppDbContextSeeder<GroupGuest, GuestSeedDto>(db, source, logger)
{
    protected override async Task<GroupGuest?> MapAsync(
        GuestSeedDto dto, CancellationToken ct = default)
    {
        var addedAt = clock.GetUtcNow().AddDays(-dto.DaysAgo);
        var token = dto.Token?.Trim() is { Length: > 0 } stated ? stated : null;

        return new GroupGuest
        {
            Id = dto.Id,
            GroupId = dto.GroupId,
            Name = dto.Name.Trim(),
            ParticipantUserId = await ParticipantFor(dto, ct),
            AddedByUserId = dto.AddedByUserId,
            AddedAt = addedAt,
            // A seeded link was issued when the guest was added, by whoever added them.
            Token = token,
            LinkIssuedAt = token is null ? null : addedAt,
            LinkIssuedByUserId = token is null ? null : dto.AddedByUserId
        };
    }

    /// <summary>
    /// Whether the row is already there -- ended ones included.
    /// </summary>
    /// <remarks>
    /// The base asks <c>Find</c>, which goes through the guest query filter: a seeded guest
    /// who has since been removed or claimed would read as missing, and the next run would
    /// insert the same id again and fail on the key. An ended guest is still the row this
    /// seed made, and is left as it is.
    /// </remarks>
    protected override Task<bool> ExistsAsync(GroupGuest entity, CancellationToken ct) =>
        DbContext.Set<GroupGuest>().IgnoreQueryFilters().AnyAsync(guest => guest.Id == entity.Id, ct);

    /// <summary>
    /// The stand-in this guest's money is recorded against, made here.
    /// </summary>
    /// <remarks>
    /// Written here rather than through <c>IGroupParticipants.StandInFor</c> because the id
    /// has to come from the seed file: a generated one would differ on every run, and the
    /// point of seeding these is that the rest of the data -- an expense they paid for, a
    /// rule that gives them a share -- can name them.
    /// <para>
    /// Re-runnable: seeding twice finds the row it made the first time rather than making a
    /// second one, the same way every other seeder here is idempotent.
    /// </para>
    /// </remarks>
    private async Task<Guid> ParticipantFor(GuestSeedDto dto, CancellationToken ct)
    {
        var id = dto.ParticipantUserId ?? dto.Id;

        if (await DbContext.Set<User>().AnyAsync(user => user.Id == id, ct))
            return id;

        // A name and nothing else: no address, and no identity, so nobody can sign in as
        // it. What every listing shows for them is that name.
        DbContext.Add(new User { Id = id, FirstName = dto.Name.Trim(), Kind = UserKind.StandIn });

        return id;
    }
}

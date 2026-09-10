using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Seeds the people who have been asked to join a group and have not answered.
/// </summary>
/// <remarks>
/// Without these, every surface that exists for invitations is empty: a group's Members tab
/// shows "Nobody is waiting to join", the home page's "Waiting on you" card never mentions
/// one, and <c>invitations list</c> answers an empty array. All three are then untestable by
/// looking, which is how the two ways in to a group came to be the least exercised part of
/// the product.
/// <para>
/// Seeded in both directions on purpose, because they are different screens. Five are in
/// groups the demo account is in, which is what the Invited card renders -- names, each with
/// a link to copy -- and one is in a group the demo account is <em>not</em> in, whose link is
/// the only way to reach the claim page. That is what Book club is for in <c>groups.json</c>:
/// claiming an invitation to a group you are already in is a state the app has nothing
/// useful to show for.
/// </para>
/// <para>
/// One of them has no <c>InvitedByUserId</c>. The column is nullable so that an account
/// deleting itself does not take the invitations it sent with it, and a nullable column
/// nothing exercises is a null-reference waiting for the first person who deletes an
/// account -- so the demo data covers it.
/// </para>
/// <para>
/// Before the expenses, and that ordering is the point rather than an accident. An invited
/// address is somebody the group can give a share to, so a group with a pending invitation
/// divides its spending between one more person than its membership -- and a demo database
/// where the invitations landed last would have every balance computed as though nobody had
/// been invited, which is precisely the behaviour this data exists to show.
/// </para>
/// </remarks>
[DependsOn(typeof(GroupSeeder))]
[DependsOn(typeof(UserSeeder))]
public class GroupInvitationSeeder(
    AppDbContext db,
    TimeProvider clock,
    ILogger<GroupInvitationSeeder> logger,
    ISeedDataSource<InvitationSeedDto> source)
    : AppDbContextSeeder<GroupInvitation, InvitationSeedDto>(db, source, logger)
{
    protected override async Task<GroupInvitation?> MapAsync(
        InvitationSeedDto dto, CancellationToken ct = default)
    {
        return new GroupInvitation
        {
            Id = dto.Id,
            GroupId = dto.GroupId,
            Name = dto.Name.Trim(),
            Token = dto.Token.Trim(),
            ParticipantUserId = await ParticipantFor(dto, ct),
            InvitedByUserId = dto.InvitedByUserId,
            InvitedAt = clock.GetUtcNow().AddDays(-dto.DaysAgo)
        };
    }

    /// <summary>
    /// The stand-in this invitation records money against, made here.
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
    private async Task<Guid> ParticipantFor(InvitationSeedDto dto, CancellationToken ct)
    {
        var id = dto.ParticipantUserId ?? dto.Id;

        if (await db.Set<User>().AnyAsync(user => user.Id == id, ct))
            return id;

        // A name and nothing else: no address, and no identity, so nobody can sign in as
        // it. What every listing shows for them is that name.
        db.Add(new User { Id = id, FirstName = dto.Name.Trim() });

        return id;
    }
}

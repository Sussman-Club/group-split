using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using GroupSplit.Seeder.Seeders.Base;
using GroupSplit.Seeder.Seeders.DTOs;

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
/// Seeded in both directions on purpose, because they are different screens. Five are
/// outgoing -- addresses invited into groups the demo account is in, which is what the
/// Invited card renders -- and one is incoming, an invitation to the demo account itself,
/// which is the only way to reach accept and decline. The incoming one needs a group the
/// demo account is <em>not</em> in, since an invitation to a group you are already in is a
/// state the app cannot produce; that is what Book club is for in <c>groups.json</c>.
/// </para>
/// <para>
/// One of them has no <c>InvitedByUserId</c>. The column is nullable so that an account
/// deleting itself does not take the invitations it sent with it, and a nullable column
/// nothing exercises is a null-reference waiting for the first person who deletes an
/// account -- so the demo data covers it.
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
    protected override Task<GroupInvitation?> MapAsync(
        InvitationSeedDto dto, CancellationToken ct = default)
    {
        return Task.FromResult<GroupInvitation?>(new GroupInvitation
        {
            Id = dto.Id,
            GroupId = dto.GroupId,
            // Lower-cased the way the API stores it, so a seeded invitation and a real one
            // for the same address are one row rather than two that never match.
            Email = dto.Email.Trim().ToLowerInvariant(),
            InvitedByUserId = dto.InvitedByUserId,
            InvitedAt = clock.GetUtcNow().AddDays(-dto.DaysAgo)
        });
    }
}

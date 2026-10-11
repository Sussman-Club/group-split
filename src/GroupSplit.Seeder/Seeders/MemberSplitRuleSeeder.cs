using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// The rule every group holds for every member: all of it is for them.
/// </summary>
/// <remarks>
/// Seeded rather than written into the group file, because nobody writes these anywhere:
/// the API provisions one as each member joins, and the migration that introduced them
/// wrote one per existing membership. A seeded database is made by putting rows in the
/// table, so it goes through neither -- and a developer opening the expense dialog would
/// find the "all for one" option missing from every group in the demo data.
/// <para>
/// No seed file and no entity of its own, so it is an <see cref="ISeeder"/> rather than a
/// <c>DbContextSeeder</c>: what it seeds is not a list somebody wrote, it is a consequence
/// of the memberships already seeded.
/// </para>
/// </remarks>
[DependsOn(typeof(GroupSeeder))]
[DependsOn(typeof(UserSeeder))]
[DependsOn(typeof(GroupGuestSeeder))]
public class MemberSplitRuleSeeder(
    AppDbContext db,
    ILogger<MemberSplitRuleSeeder> logger,
    IMemberSplitRules memberRules) : ISeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var groups = await db.Set<Group>()
            // Not the personal groups, which divide nothing and so have no rules.
            .Where(@group => !db.Set<User>().Any(user => user.Id == @group.Id))
            .Include(@group => @group.Users).ToListAsync(ct);

        // Counted once either side rather than around every call: the number is only for the
        // log line, and each member's and invitee's rule is exactly one provisioned rule.
        var before = await Provisioned(ct);

        foreach (var group in groups)
        {
            // Find-or-create, so running the seeder twice writes nothing the second time --
            // the same property every other seeder here has.
            foreach (var member in group.Users)
                await memberRules.EnsureFor(group, member, ct);
        }

        // And every guest, who has one as well from the moment they are added. The seeded
        // guests are written straight into the table too, so the demo would otherwise show
        // nobody under "One pays" for them.
        var guests = await db.Set<GroupGuest>()
            .Include(guest => guest.Group)
            .Include(guest => guest.Participant)
            .ToListAsync(ct);

        foreach (var guest in guests)
            await memberRules.EnsureFor(guest.Group, guest.Participant, ct);

        logger.LogInformation("Seeded {Count} {Entity}", await Provisioned(ct) - before, "MemberSplitRule");
    }

    private Task<int> Provisioned(CancellationToken ct) =>
        db.Set<SplitRule>().CountAsync(rule => rule.BuiltIn &&
                                               rule.Versions.OfType<SoleSplitRuleVersion>()
                                                   .Any(version => version.SupersededAt == null), ct);
}

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
public class MemberSplitRuleSeeder(
    AppDbContext db,
    ILogger<MemberSplitRuleSeeder> logger,
    IMemberSplitRules memberRules) : ISeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var groups = await db.Set<Group>().Include(@group => @group.Users).ToListAsync(ct);

        var seeded = 0;

        foreach (var group in groups)
        {
            foreach (var member in group.Users)
            {
                // Find-or-create, so running the seeder twice writes nothing the second
                // time -- the same property every other seeder here has.
                var before = await db.Set<SplitRule>()
                    .CountAsync(rule => rule.Group.Id == group.Id, ct);

                await memberRules.EnsureFor(group, member, ct);

                var after = await db.Set<SplitRule>()
                    .CountAsync(rule => rule.Group.Id == group.Id, ct);

                seeded += after - before;
            }
        }

        logger.LogInformation("Seeded {Count} {Entity}", seeded, "MemberSplitRule");
    }
}

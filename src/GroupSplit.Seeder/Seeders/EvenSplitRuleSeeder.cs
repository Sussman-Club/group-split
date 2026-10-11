using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Ensures every seeded group has the built-in "Even" rule, and divides by it whatever no
/// category says.
/// </summary>
/// <remarks>
/// The API gives a group both as it is created, and the migration that introduced them gave
/// them to every group that already existed. A seeded group goes through neither: the
/// migration runs before the seeder writes a single group, and the seeder writes rows rather
/// than calling the API. Without this the demo data was the one place "Even" could not be
/// chosen and <c>groups show</c> named no default split.
/// <para>
/// A group that already names a default keeps it -- that is a choice somebody made, and
/// re-running the seeder against a database somebody has used must not undo it.
/// </para>
/// </remarks>
[DependsOn(typeof(GroupSeeder))]
public sealed class EvenSplitRuleSeeder(
    AppDbContext db,
    ILogger<EvenSplitRuleSeeder> logger,
    IEvenSplitRule evenRule) : ISeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var groups = await db.Set<Group>()
            // Not the personal groups, which divide nothing and so have no rules.
            .Where(@group => !db.Set<User>().Any(user => user.Id == @group.Id))
            .ToListAsync(ct);
        var defaulted = 0;

        foreach (var group in groups)
        {
            // Find-or-create, so running the seeder twice writes no second rule.
            var rule = await evenRule.EnsureFor(group, ct);

            if (group.DefaultSplitRuleId is not null)
                continue;

            group.DefaultSplitRule = rule;
            defaulted++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Defaulted {Count} {Entity} to the Even rule", defaulted, "Group");
    }
}

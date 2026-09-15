using GroupSplit.API.Services;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Seeder.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Seeder.Seeders;

/// <summary>
/// Ensures every seeded group has the built-in rule used to divide an expense by its bill.
/// </summary>
/// <remarks>
/// Groups can already exist when this rule is introduced, so provisioning it only from the
/// group-creation path leaves those groups without the option in the expense editor. This
/// idempotent seeder repairs both a fresh database and an existing seeded database.
/// </remarks>
[DependsOn(typeof(GroupSeeder))]
public sealed class BillSplitRuleSeeder(
    AppDbContext db,
    ILogger<BillSplitRuleSeeder> logger,
    IBillSplitRule billRule) : ISeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var groups = await db.Set<Group>().ToListAsync(ct);
        var seeded = 0;

        foreach (var group in groups)
        {
            var before = await db.Set<SplitRule>()
                .CountAsync(rule => rule.Group.Id == group.Id, ct);

            await billRule.EnsureFor(group, ct);

            var after = await db.Set<SplitRule>()
                .CountAsync(rule => rule.Group.Id == group.Id, ct);

            seeded += after - before;
        }

        logger.LogInformation("Seeded {Count} {Entity}", seeded, "BillSplitRule");
    }
}

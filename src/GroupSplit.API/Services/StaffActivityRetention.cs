using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>Removes staff activity after seven days; ticket messages and status history are separate.</summary>
public sealed class StaffActivityRetention(AppDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int BatchSize = 1000;

    public async Task<int> PruneAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - Retention;
        var deleted = 0;
        while (true)
        {
            var ids = await db.Set<StaffActivity>().Where(a => a.At < cutoff)
                .OrderBy(a => a.At).ThenBy(a => a.Id).Select(a => a.Id).Take(BatchSize).ToListAsync(ct);
            if (ids.Count == 0) break;
            if (db.Database.IsRelational())
                deleted += await db.Set<StaffActivity>().Where(a => ids.Contains(a.Id) && a.At < cutoff).ExecuteDeleteAsync(ct);
            else
            {
                var rows = await db.Set<StaffActivity>().Where(a => ids.Contains(a.Id) && a.At < cutoff).ToListAsync(ct);
                db.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                deleted += rows.Count;
            }
            if (ids.Count < BatchSize) break;
        }
        return deleted;
    }
}

public sealed record SweepStaffActivity : IJob;
internal sealed class SweepStaffActivityHandler(StaffActivityRetention retention, ILogger<SweepStaffActivityHandler> logger) : IJobHandler<SweepStaffActivity>
{
    public async ValueTask HandleAsync(SweepStaffActivity job, CancellationToken cancellationToken)
    {
        var removed = await retention.PruneAsync(cancellationToken);
        if (removed > 0) logger.LogInformation("Removed {Count} expired staff activity entries.", removed);
    }
}

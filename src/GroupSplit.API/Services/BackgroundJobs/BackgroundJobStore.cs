using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.BackgroundJobs;

public interface IBackgroundJobStore
{
    Task<BackgroundJob?> Claim(CancellationToken ct);
    Task Finish(BackgroundJob job, BackgroundJobStatus status, string? reason, CancellationToken ct);
}

/// <summary>Short, atomic database operations; no transaction spans handler execution.</summary>
public sealed class BackgroundJobStore(AppDbContext db, TimeProvider time) : IBackgroundJobStore
{
    internal const int MaxAttempts = 10;
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<BackgroundJob?> Claim(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lease = Guid.NewGuid();
        var expires = now + LeaseDuration;
        // One statement also makes this safe with EF's retry strategy: an ambiguous
        // commit may leave a lease behind, which expires instead of losing the job.
        var rows = await db.Set<BackgroundJob>().FromSqlInterpolated($"""
            WITH candidate AS (
                SELECT "Id" FROM "BackgroundJob"
                WHERE ("Status" = 'Pending' AND "AvailableAt" <= {now})
                   OR ("Status" = 'Running' AND "LeaseExpiresAt" <= {now})
                ORDER BY "AvailableAt", "CreatedAt", "Id"
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE "BackgroundJob" AS job
            SET "Status" = CASE WHEN job."Attempts" >= {MaxAttempts} THEN 'Failed' ELSE 'Running' END,
                "Attempts" = CASE WHEN job."Attempts" >= {MaxAttempts} THEN job."Attempts" ELSE job."Attempts" + 1 END,
                "LeaseId" = CASE WHEN job."Attempts" >= {MaxAttempts} THEN NULL ELSE {lease} END,
                "LeaseExpiresAt" = CASE WHEN job."Attempts" >= {MaxAttempts} THEN NULL ELSE {expires} END,
                "Reason" = CASE WHEN job."Attempts" >= {MaxAttempts} THEN 'AttemptsExhausted' ELSE job."Reason" END,
                "FinishedAt" = CASE WHEN job."Attempts" >= {MaxAttempts} THEN {now} ELSE NULL END
            FROM candidate WHERE job."Id" = candidate."Id"
            RETURNING job.*
            """).AsNoTracking().ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public async Task Finish(BackgroundJob job, BackgroundJobStatus status, string? reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (status == BackgroundJobStatus.Pending && job.Attempts >= MaxAttempts)
            status = BackgroundJobStatus.Failed;
        var terminal = status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed;
        var next = now + TimeSpan.FromSeconds(Math.Min(3600, 5 * Math.Pow(2, job.Attempts - 1)));
        // Fencing prevents a late handler from acknowledging another worker's lease.
        await db.Set<BackgroundJob>()
            .Where(row => row.Id == job.Id && row.Status == BackgroundJobStatus.Running && row.LeaseId == job.LeaseId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Status, status)
                .SetProperty(row => row.Reason, reason)
                .SetProperty(row => row.AvailableAt, next)
                .SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(row => row.FinishedAt, terminal ? now : (DateTimeOffset?)null)
                .SetProperty(row => row.Payload, status == BackgroundJobStatus.Completed ? "{}" : job.Payload), ct);
    }
}

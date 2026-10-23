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
        // Bound contention work; the existing scheduler will try again on its next tick.
        for (var conflict = 0; conflict < 16; conflict++)
        {
            var now = time.GetUtcNow();
            var candidate = await db.Set<BackgroundJob>().AsNoTracking()
                .Where(job => (job.Status == BackgroundJobStatus.Pending && job.AvailableAt <= now)
                    || (job.Status == BackgroundJobStatus.Running && job.LeaseExpiresAt <= now))
                .OrderBy(job => job.AvailableAt).ThenBy(job => job.CreatedAt).ThenBy(job => job.Id)
                .FirstOrDefaultAsync(ct);
            if (candidate is null) return null;
            if (await TryClaim(candidate, now, ct)) return candidate;
        }
        return null;
    }

    private async Task<bool> TryClaim(BackgroundJob candidate, DateTimeOffset now, CancellationToken ct)
    {
        var exhausted = candidate.Attempts >= MaxAttempts;
        var status = exhausted ? BackgroundJobStatus.Failed : BackgroundJobStatus.Running;
        var attempts = exhausted ? candidate.Attempts : candidate.Attempts + 1;
        Guid? lease = exhausted ? null : Guid.NewGuid();
        DateTimeOffset? expires = exhausted ? null : now + LeaseDuration;
        DateTimeOffset? finished = exhausted ? now : null;
        var reason = exhausted ? "AttemptsExhausted" : candidate.Reason;

        // Compare the observed state in the UPDATE itself. Only one competing worker
        // can change it; losers select again. No transaction spans handler execution.
        var affected = await db.Set<BackgroundJob>()
            .Where(job => job.Id == candidate.Id && job.Status == candidate.Status
                && job.Attempts == candidate.Attempts && job.LeaseId == candidate.LeaseId
                && job.AvailableAt == candidate.AvailableAt && job.LeaseExpiresAt == candidate.LeaseExpiresAt)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, status)
                .SetProperty(job => job.Attempts, attempts)
                .SetProperty(job => job.LeaseId, lease)
                .SetProperty(job => job.LeaseExpiresAt, expires)
                .SetProperty(job => job.FinishedAt, finished)
                .SetProperty(job => job.Reason, reason), ct);
        if (affected == 0) return false;

        candidate.Status = status;
        candidate.Attempts = attempts;
        candidate.LeaseId = lease;
        candidate.LeaseExpiresAt = expires;
        candidate.FinishedAt = finished;
        candidate.Reason = reason;
        return true;
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

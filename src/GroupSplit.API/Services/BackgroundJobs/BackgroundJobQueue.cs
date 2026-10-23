using System.Text.Json;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;

namespace GroupSplit.API.Services.BackgroundJobs;

public interface IBackgroundJobQueue
{
    /// <summary>Stages an invocation. The caller saves it with its business changes.</summary>
    BackgroundJob Enqueue(IJob job);
    /// <summary>Records work which cannot safely run without an operator.</summary>
    BackgroundJob AwaitOperator<T>(string type, T details, string reason);
}

public sealed class BackgroundJobQueue(AppDbContext db, BackgroundJobRegistry registry, TimeProvider time)
    : IBackgroundJobQueue
{
    public BackgroundJob Enqueue(IJob job)
    {
        var wire = registry.Serialize(job);
        return Add(new BackgroundJob
        {
            Type = wire.Name, Version = wire.Version, Payload = wire.Payload,
            CreatedAt = time.GetUtcNow(), AvailableAt = time.GetUtcNow()
        });
    }

    public BackgroundJob AwaitOperator<T>(string type, T details, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (type.Length > 128 || reason.Length > 256) throw new ArgumentOutOfRangeException(nameof(reason));
        return Add(new BackgroundJob
        {
            Type = type, Payload = JsonSerializer.Serialize(details), Reason = reason,
            Status = BackgroundJobStatus.AwaitingOperator,
            CreatedAt = time.GetUtcNow(), AvailableAt = time.GetUtcNow()
        });
    }

    private BackgroundJob Add(BackgroundJob job) { db.Add(job); return job; }
}

using GroupSplit.Data.Entities;
using GroupSplit.Jobs;

namespace GroupSplit.API.Services.BackgroundJobs;

public interface IBackgroundJobProcessor
{
    Task<bool> RunNext(CancellationToken ct);
}

/// <summary>Handles persistence around an invocation; scheduling and workers belong to GroupSplit.Jobs.</summary>
public sealed class BackgroundJobProcessor(
    IServiceScopeFactory scopes, BackgroundJobRegistry registry, ILogger<BackgroundJobProcessor> logger)
    : IBackgroundJobProcessor
{
    public async Task<bool> RunNext(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackgroundJobStore>();
        var row = await store.Claim(ct);
        if (row is null) return false;
        if (row.Status != BackgroundJobStatus.Running) return true;

        IJob job;
        try { job = registry.Deserialize(row.Type, row.Version, row.Payload); }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException
                                     or NotSupportedException)
        {
            await store.Finish(row, BackgroundJobStatus.AwaitingOperator, "InvalidJobContract", ct);
            return true;
        }

        var status = BackgroundJobStatus.Completed;
        string? reason = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await scope.ServiceProvider.GetRequiredService<IJobExecutor>().ExecuteAsync(job, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Keep the lease: another process recovers it even if shutdown cannot save.
            return true;
        }
        catch (Exception error)
        {
            status = BackgroundJobStatus.Pending;
            reason = timeout.IsCancellationRequested ? "ExecutionTimedOut" : "HandlerFailed";
            logger.LogWarning("Durable job {JobId} attempt {Attempt} failed ({ErrorType})",
                row.Id, row.Attempts, error.GetType().Name);
        }
        // A handler may have tracked partial changes before throwing. Persist its outcome
        // in a fresh scope, without accidentally saving those business changes.
        await using var outcome = scopes.CreateAsyncScope();
        await outcome.ServiceProvider.GetRequiredService<IBackgroundJobStore>().Finish(row, status, reason, ct);
        return true;
    }
}

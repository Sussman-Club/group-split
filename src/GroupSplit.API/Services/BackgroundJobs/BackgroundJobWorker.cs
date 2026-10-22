using GroupSplit.Data.Entities;
using GroupSplit.Jobs;

namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Runs persisted jobs through the existing scoped job executor.</summary>
public sealed class BackgroundJobWorker(
    IServiceScopeFactory scopes, BackgroundJobRegistry registry, ILogger<BackgroundJobWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunNext(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Neither payloads nor exception messages belong in job logs.
                logger.LogError("Durable job polling failed ({ErrorType})", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<bool> RunNext(CancellationToken ct)
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

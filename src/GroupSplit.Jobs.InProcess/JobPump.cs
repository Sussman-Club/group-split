using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Jobs.InProcess;

/// <summary>
/// Drains <see cref="InMemoryJobQueue"/> into <see cref="IJobDispatcher"/>, one job at a
/// time, for as long as the host runs.
/// </summary>
/// <remarks>
/// The counterpart of a queue trigger, and the only part of the arrangement a cloud
/// deployment throws away: there, the platform reads the queue and calls the dispatcher
/// itself.
/// <para>
/// Every job is caught. One job's failure is one log line, because the alternative is a
/// pump that dies on a bad message and takes every later job with it.
/// </para>
/// </remarks>
internal sealed class JobPump(
    InMemoryJobQueue queue,
    IJobDispatcher dispatcher,
    ILogger<JobPump> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var envelope in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await dispatcher.DispatchAsync(envelope, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Job {JobType} enqueued at {EnqueuedAt} failed.",
                        envelope.JobType, envelope.EnqueuedAt);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping, which is the only way out of the loop.
        }
    }
}

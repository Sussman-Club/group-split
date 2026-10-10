using Microsoft.Extensions.Logging;

namespace GroupSplit.Jobs.Defaults;

internal static class JobProcessing
{
    /// <summary>
    /// Runs every delivery the receiver hands over until it stops or the host does.
    /// </summary>
    /// <remarks>
    /// Every failure is logged here, whoever dispatched the job. Most callers dispatch and
    /// move on without keeping the handle, so the faulted handle is no place for a failure
    /// to end up alone: a sync that died after the webhook had already been answered would
    /// otherwise leave no trace anywhere. The job itself is logged as its inputs, which is
    /// what makes "it failed" into "it failed for that connection" -- jobs are requests
    /// that name things by id, and one that ever has to carry something secret overrides
    /// its <see cref="object.ToString"/> to leave it out.
    /// <para>
    /// A cancellation is one of two things, and only the first is expected: the host
    /// stopping, which abandons whatever was running, or the job cancelling itself while
    /// the host was not -- an HTTP client's timeout is the usual one -- which is a failure
    /// that happens to wear <see cref="OperationCanceledException"/>. The second is logged
    /// and delivered as the failure it is, so whoever waits on the handle sees the timeout
    /// rather than a cancellation nobody asked for.
    /// </para>
    /// </remarks>
    internal static async Task RunAsync(IJobReceiver receiver,
        Func<IJob, CancellationToken, ValueTask<object?>> execute, ILogger logger,
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in receiver.ReceiveAsync(stoppingToken))
            {
                var job = delivery.Job;
                object? result;
                try
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    result = await execute(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Job {JobType} was stopped by shutdown: {Job}.",
                        job.GetType().Name, job.ToString());
                    await delivery.CancelAsync();
                    continue;
                }
                catch (OperationCanceledException exception)
                {
                    logger.LogError(exception,
                        "Job {JobType} was cancelled while the host was still running -- a timeout, or the "
                        + "job giving up, not a shutdown: {Job}.",
                        job.GetType().Name, job.ToString());
                    await delivery.FailAsync(exception);
                    continue;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Job {JobType} failed: {Job}.", job.GetType().Name, job.ToString());
                    await delivery.FailAsync(exception);
                    continue;
                }
                await delivery.CompleteAsync(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

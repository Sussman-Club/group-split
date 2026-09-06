using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Jobs.InProcess;

/// <summary>
/// Enqueues each <see cref="RecurringJobs"/> registration on its period.
/// </summary>
/// <remarks>
/// The in-process stand-in for a scheduler somebody else runs. It enqueues rather than
/// running anything itself, so a scheduled job takes exactly the path an asked-for one
/// takes and there is no second way for work to happen.
/// <para>
/// A deployment that schedules from outside simply does not start this, and the jobs it
/// would have enqueued arrive from the schedule instead.
/// </para>
/// <para>
/// Every await is <c>ConfigureAwait(false)</c>, as library code should be: this runs from
/// whatever thread started the host and must never resume on a context it did not ask for.
/// </para>
/// </remarks>
internal sealed class RecurringJobScheduler(
    RecurringJobs jobs,
    IJobQueue queue,
    TimeProvider clock,
    ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    private readonly TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once every registration's first wait has been placed on the clock.
    /// </summary>
    /// <remarks>
    /// <see cref="BackgroundService.StartAsync"/> does not run <see cref="ExecuteAsync"/>
    /// to its first await before returning, so "started" and "scheduled" are different
    /// moments. Under a real clock nobody can tell; under a controlled one, advancing time
    /// in between means advancing past a wait that does not exist yet. This is how a test
    /// knows the difference, and it is the reason the waits below are all created before
    /// any of them is awaited.
    /// </remarks>
    internal Task Scheduled => _scheduled.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every first wait is placed on the clock here, before any of them is awaited.
        var pending = jobs.All
            .Select(job => (Job: job, FirstRun: FirstRunAsync(job, stoppingToken)))
            .ToList();

        _scheduled.TrySetResult();

        await Task.WhenAll(pending.Select(entry => RunAsync(entry.Job, entry.FirstRun, stoppingToken)))
            .ConfigureAwait(false);
    }

    private Task FirstRunAsync(RecurringJob job, CancellationToken ct)
    {
        logger.LogInformation("Recurring job {JobType} starts in {InitialDelay} and repeats every {Period}.",
            job.JobType, job.InitialDelay, job.Period);

        return Task.Delay(job.InitialDelay, clock, ct);
    }

    private async Task RunAsync(RecurringJob job, Task firstRun, CancellationToken ct)
    {
        try
        {
            await firstRun.ConfigureAwait(false);

            using var timer = new PeriodicTimer(job.Period, clock);

            do
            {
                try
                {
                    // job.Enqueue, not queue.EnqueueAsync(job): the queue's own generic
                    // method would happily take a RecurringJob as the job, and an instance
                    // method beats any extension trying to help.
                    await job.Enqueue(queue, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Could not enqueue the recurring job {JobType}; next attempt in {Period}.",
                        job.JobType, job.Period);
                }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception e)
        {
            // Nothing awaits this task once the host has started, so an exception here
            // would otherwise vanish and take the schedule with it: a daily job that
            // silently never runs again.
            logger.LogError(e, "Recurring job {JobType} stopped being scheduled.", job.JobType);
        }
    }
}

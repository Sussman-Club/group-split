using Microsoft.Extensions.Logging;

namespace GroupSplit.Jobs.Defaults;

internal sealed class DefaultJobScheduler(
    IJobDispatcher dispatcher, TimeProvider clock, ILogger<DefaultJobScheduler> logger) : IJobScheduler
{
    private readonly object _gate = new();
    private readonly List<DefaultScheduledJob> _jobs = [];
    private bool _stopped;

    public ValueTask<IScheduledJob> ScheduleAsync(
        IJob job, JobSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(schedule);
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        DateTimeOffset? next = schedule switch
        {
            JobSchedule.OnceSchedule once => once.At,
            JobSchedule.EverySchedule every when every.Interval > TimeSpan.Zero => every.FirstRun,
            JobSchedule.EverySchedule => throw new ArgumentOutOfRangeException(nameof(schedule), "Interval must be positive."),
            JobSchedule.CronSchedule calendar => ParseCron(calendar),
            _ => throw new ArgumentException("Unsupported schedule.", nameof(schedule))
        };
        if (next is null) throw new ArgumentException("Schedule has no future occurrence.", nameof(schedule));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            var registration = new DefaultScheduledJob(this, job, schedule, next.Value);
            _jobs.Add(registration);
            return ValueTask.FromResult<IScheduledJob>(registration);
        }

        DateTimeOffset? ParseCron(JobSchedule.CronSchedule calendar) => calendar.Expression.GetNextOccurrence(now, calendar.TimeZone);
    }

    public ValueTask<IReadOnlyList<IScheduledJob>> GetScheduledJobsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<IScheduledJob>>(_jobs.Cast<IScheduledJob>().ToArray());
    }

    internal void Cancel(DefaultScheduledJob job)
    {
        lock (_gate) _jobs.Remove(job);
    }

    internal async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        DefaultScheduledJob[] due;
        lock (_gate) due = _jobs.Where(job => !job.Dispatching && job.Next <= clock.GetUtcNow()).ToArray();
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_jobs.Contains(job) || job.Dispatching || job.Next > clock.GetUtcNow()) continue;
                job.Dispatching = true;
            }
            var succeeded = false;
            try
            {
                await dispatcher.DispatchAsync(job.Job, cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogError(error, "Could not dispatch scheduled job {JobType}.", job.Job.GetType().Name);
            }
            finally
            {
                lock (_gate)
                {
                    job.Dispatching = false;
                    if (_jobs.Contains(job))
                    {
                        var next = Next(job, succeeded);
                        if (next is null) _jobs.Remove(job);
                        else job.Next = next.Value;
                    }
                }
            }
        }
    }

    private DateTimeOffset? Next(DefaultScheduledJob job, bool succeeded)
    {
        var now = clock.GetUtcNow();
        return job.Schedule switch
        {
            JobSchedule.OnceSchedule => succeeded ? null : now.AddSeconds(1),
            JobSchedule.CronSchedule calendar => calendar.Expression.GetNextOccurrence(now, calendar.TimeZone),
            JobSchedule.EverySchedule every => NextInterval(every, now),
            _ => null
        };
    }

    private static DateTimeOffset? NextInterval(JobSchedule.EverySchedule every, DateTimeOffset now)
    {
        if (now < every.FirstRun) return every.FirstRun;
        var elapsed = now.UtcTicks - every.FirstRun.UtcTicks;
        var remainder = elapsed % every.Interval.Ticks;
        var advance = every.Interval.Ticks - remainder;
        return advance > DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks ? null : now.AddTicks(advance);
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _jobs.Clear();
        }
    }
}

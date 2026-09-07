namespace GroupSplit.Jobs;

public interface IJobScheduler
{
    ValueTask<IScheduledJob> ScheduleAsync(
        IJob job, JobSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>Returns a finite snapshot. Retention of completed schedules is implementation-specific.</summary>
    ValueTask<IReadOnlyList<IScheduledJob>> GetScheduledJobsAsync(
        CancellationToken cancellationToken = default);
}

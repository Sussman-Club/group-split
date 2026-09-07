namespace GroupSplit.Jobs.Standalone;

public sealed class SchedulerBuilder(JobsBuilder jobsBuilder)
    : IJobSchedulerBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder, SchedulerBuilder>
{
    public JobsBuilder JobsBuilder { get; } = jobsBuilder;

    public SchedulerBuilder Use(IJobScheduler scheduler)
    {
        JobsBuilder.EnsureMutable();
        ArgumentNullException.ThrowIfNull(scheduler);
        JobsBuilder.Inner.Scheduler.Use(_ => new BorrowedScheduler(scheduler));
        return this;
    }

    public SchedulerBuilder Add(IJob job, JobSchedule schedule)
    {
        JobsBuilder.EnsureMutable();
        JobsBuilder.Inner.Scheduler.Add(job, schedule);
        return this;
    }

    IJobScheduler IJobSchedulerBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder, SchedulerBuilder>
        .BuildScheduler(IServiceProvider services) => JobsBuilder.Inner.Scheduler.BuildScheduler(services);

    private sealed class BorrowedScheduler(IJobScheduler scheduler) : IJobScheduler
    {
        public ValueTask<IScheduledJob> ScheduleAsync(IJob job, JobSchedule schedule, CancellationToken cancellationToken = default) =>
            scheduler.ScheduleAsync(job, schedule, cancellationToken);
        public ValueTask<IReadOnlyList<IScheduledJob>> GetScheduledJobsAsync(CancellationToken cancellationToken = default) =>
            scheduler.GetScheduledJobsAsync(cancellationToken);
    }
}

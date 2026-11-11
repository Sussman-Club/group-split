namespace GroupSplit.Jobs.Defaults;

internal sealed class DefaultScheduledJob(
    DefaultJobScheduler owner, IJob job, JobSchedule schedule, DateTimeOffset next) : IScheduledJob
{
    public IJob Job { get; } = job;
    public JobSchedule Schedule { get; } = schedule;
    internal DateTimeOffset Next { get; set; } = next;
    internal bool Dispatching { get; set; }

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        owner.Cancel(this);
        return ValueTask.CompletedTask;
    }
}

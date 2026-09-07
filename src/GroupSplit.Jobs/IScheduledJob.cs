namespace GroupSplit.Jobs;

public interface IScheduledJob
{
    IJob Job { get; }
    JobSchedule Schedule { get; }

    /// <summary>Stops future dispatches. An occurrence already being dispatched may still run.</summary>
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

namespace GroupSplit.Jobs;

/// <summary>
/// Jobs that should happen on a period rather than because something asked.
/// </summary>
/// <remarks>
/// A registration, not a mechanism. In this process <c>AddInProcessJobs</c> starts a timer
/// that enqueues each of these; in a cloud deployment a schedule does it from outside and
/// nothing here runs. Either way the list says what the system expects to happen and how
/// often, in one place, which is the part worth keeping when the mechanism changes.
/// </remarks>
public sealed class RecurringJobs
{
    private readonly List<RecurringJob> _jobs = [];

    public IReadOnlyList<RecurringJob> All => _jobs;

    internal void Add(RecurringJob job) => _jobs.Add(job);
}

/// <summary>
/// One recurring registration: what to enqueue, how often, and how long to wait first.
/// </summary>
/// <param name="InitialDelay">
/// Kept away from zero on purpose. A deployment that restarts every instance at once should
/// not have all of them hit whatever the job talks to in the same second.
/// </param>
public sealed record RecurringJob(
    string JobType,
    TimeSpan Period,
    TimeSpan InitialDelay,
    Func<IJobQueue, CancellationToken, Task> Enqueue);

namespace GroupSplit.Jobs;

/// <summary>
/// Somewhere to send work that should happen, but not inside the request asking for it.
/// </summary>
/// <remarks>
/// The whole of what a caller needs to know. It says nothing about where the work goes or
/// what runs it: today an in-process channel drained by a hosted service, tomorrow a cloud
/// queue drained by a function, and the code that enqueues does not change either time.
/// <para>
/// Enqueuing is not running. It returns once the job is somewhere durable enough for the
/// implementation's promises, which for the in-memory one is "in this process's memory" --
/// so a restart loses what it held. Anything that must survive a restart needs a queue that
/// says so, and a sweep that re-enqueues what was missed is the cheap way to not care.
/// </para>
/// </remarks>
public interface IJobQueue
{
    Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IJob;
}

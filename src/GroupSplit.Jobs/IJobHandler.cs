namespace GroupSplit.Jobs;

/// <summary>
/// What to do with one job. The part that is the same in every host.
/// </summary>
/// <remarks>
/// A handler is resolved from a scope of its own, so it may take scoped services -- a
/// <c>DbContext</c>, a repository -- exactly as an endpoint's service would.
/// <para>
/// Throwing means the job did not happen. What follows is the host's business: the
/// in-process pump logs it and moves on, and a cloud queue will redeliver by its own rules.
/// That is why there is no retry count here: retrying is the transport's job, and building
/// a second retry scheme on top of one that already exists is how a job runs nine times.
/// </para>
/// </remarks>
public interface IJobHandler<in TJob> where TJob : IJob
{
    Task HandleAsync(TJob job, CancellationToken ct = default);
}

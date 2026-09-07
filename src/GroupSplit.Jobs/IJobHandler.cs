namespace GroupSplit.Jobs;

public interface IJobHandler<in TJob> where TJob : IJob
{
    ValueTask HandleAsync(TJob job, CancellationToken cancellationToken);
}

public interface IJobHandler<in TJob, TResult> where TJob : IJob<TResult>
{
    ValueTask<TResult> HandleAsync(TJob job, CancellationToken cancellationToken);
}

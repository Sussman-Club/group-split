namespace GroupSplit.Jobs;

public interface IJobDispatcher
{
    Task<IJobHandle> DispatchAsync(
        IJob job, CancellationToken cancellationToken = default);

    Task<IJobHandle<TResult>> DispatchAsync<TResult>(
        IJob<TResult> job, CancellationToken cancellationToken = default);
}

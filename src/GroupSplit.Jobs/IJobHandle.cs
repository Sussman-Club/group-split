namespace GroupSplit.Jobs;

public interface IJobHandle
{
    ValueTask WaitAsync(CancellationToken cancellationToken);
}

public interface IJobHandle<TResult> : IJobHandle
{
    ValueTask<TResult> GetResultAsync(CancellationToken cancellationToken);

    async ValueTask IJobHandle.WaitAsync(CancellationToken cancellationToken)
    {
        await GetResultAsync(cancellationToken);
    }
}

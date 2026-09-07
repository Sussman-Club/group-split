namespace GroupSplit.Jobs.Defaults;

public class DefaultJobHandle(Task task) : IJobHandle
{
    public async ValueTask WaitAsync(CancellationToken cancellationToken) =>
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
}

public class DefaultJobHandle<TResult>(Task<object?> task)
    : DefaultJobHandle(task), IJobHandle<TResult>
{
    public async ValueTask<TResult> GetResultAsync(CancellationToken cancellationToken) =>
        (TResult)(await task.WaitAsync(cancellationToken).ConfigureAwait(false))!;
}

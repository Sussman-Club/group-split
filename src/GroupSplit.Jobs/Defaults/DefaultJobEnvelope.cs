namespace GroupSplit.Jobs.Defaults;

internal sealed class DefaultJobEnvelope(IJob job) : IJobDelivery
{
    private readonly TaskCompletionSource<object?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IJob Job { get; } = job;
    internal Task<object?> Completion => _completion.Task;

    public ValueTask CompleteAsync(object? result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _completion.TrySetResult(result);
        return ValueTask.CompletedTask;
    }

    public ValueTask FailAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _completion.TrySetException(exception);
        return ValueTask.CompletedTask;
    }

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _completion.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
}

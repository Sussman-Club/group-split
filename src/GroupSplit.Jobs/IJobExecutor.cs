namespace GroupSplit.Jobs;

/// <summary>Executes a registered handler in the caller's service scope.</summary>
/// <remarks>Transports provide delivery and acknowledgement; the executor provides neither.</remarks>
public interface IJobExecutor
{
    ValueTask<object?> ExecuteAsync(IJob job, CancellationToken cancellationToken);
}

internal sealed record VoidResult;

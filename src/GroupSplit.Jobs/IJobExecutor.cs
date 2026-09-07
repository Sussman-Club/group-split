namespace GroupSplit.Jobs;

internal interface IJobExecutor
{
    ValueTask<object?> ExecuteAsync(IJob job, CancellationToken cancellationToken);
}

internal sealed record VoidResult;

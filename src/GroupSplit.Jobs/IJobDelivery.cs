namespace GroupSplit.Jobs;

/// <summary>A received job and the means to report its terminal outcome.</summary>
public interface IJobDelivery
{
    IJob Job { get; }

    ValueTask CompleteAsync(object? result, CancellationToken cancellationToken = default);
    ValueTask FailAsync(Exception exception, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

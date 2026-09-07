namespace GroupSplit.Jobs.Defaults;

internal static class JobProcessing
{
    internal static async Task RunAsync(IJobReceiver receiver,
        Func<IJob, CancellationToken, ValueTask<object?>> execute, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in receiver.ReceiveAsync(stoppingToken))
            {
                object? result;
                try
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    result = await execute(delivery.Job, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    await delivery.CancelAsync();
                    continue;
                }
                catch (Exception exception)
                {
                    await delivery.FailAsync(exception);
                    continue;
                }
                await delivery.CompleteAsync(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

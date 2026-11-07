using GroupSplit.Jobs;

namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Drains a bounded batch using the existing scheduled-job pipeline.</summary>
public sealed record ProcessBackgroundJobs : IJob;

internal sealed class ProcessBackgroundJobsHandler(
    IBackgroundJobProcessor processor, IJobDispatcher dispatcher, ILogger<ProcessBackgroundJobsHandler> logger)
    : IJobHandler<ProcessBackgroundJobs>
{
    public async ValueTask HandleAsync(ProcessBackgroundJobs job, CancellationToken cancellationToken)
    {
        try
        {
            for (var count = 0; count < 10; count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await processor.RunNext(cancellationToken)) return;
            }
            // Yield the existing executor after a bounded batch, but keep draining a
            // backlog immediately instead of limiting throughput to ten jobs per minute.
            await dispatcher.DispatchAsync(new ProcessBackgroundJobs(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // The existing generic job runner logs exceptions. Keep storage exception
            // messages (which may include payload parameters) out of that pipeline.
            logger.LogError("Durable job polling failed ({ErrorType})", error.GetType().Name);
        }
    }
}

using GroupSplit.Jobs;

namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Drains a bounded batch using the existing scheduled-job pipeline.</summary>
public sealed record ProcessBackgroundJobs : IJob;

internal sealed class ProcessBackgroundJobsHandler(
    IBackgroundJobProcessor processor, ILogger<ProcessBackgroundJobsHandler> logger)
    : IJobHandler<ProcessBackgroundJobs>
{
    public async ValueTask HandleAsync(ProcessBackgroundJobs job, CancellationToken cancellationToken)
    {
        try
        {
            for (var count = 0; count < 10; count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await processor.RunNext(cancellationToken)) break;
            }
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

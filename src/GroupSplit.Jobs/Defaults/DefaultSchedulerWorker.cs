using Microsoft.Extensions.Hosting;

namespace GroupSplit.Jobs.Defaults;

internal sealed class DefaultSchedulerWorker(IJobScheduler selectedScheduler, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (selectedScheduler is not DefaultJobScheduler scheduler) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
        try
        {
            do
            {
                await scheduler.ProcessDueAsync(stoppingToken).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { scheduler.Stop(); }
    }
}

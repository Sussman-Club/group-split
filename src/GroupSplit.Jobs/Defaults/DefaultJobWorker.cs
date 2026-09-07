using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Jobs.Defaults;

internal sealed class DefaultJobWorker(
    IJobReceiver receiver,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        JobProcessing.RunAsync(receiver, ExecuteJobAsync, stoppingToken);

    private async ValueTask<object?> ExecuteJobAsync(IJob job, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobExecutor>()
            .ExecuteAsync(job, cancellationToken);
    }
}

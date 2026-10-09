using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GroupSplit.Jobs.Defaults;

internal sealed class DefaultJobWorker(
    IJobReceiver receiver,
    IServiceScopeFactory scopeFactory,
    ILogger<DefaultJobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        JobProcessing.RunAsync(receiver, ExecuteJobAsync, logger, stoppingToken);

    private async ValueTask<object?> ExecuteJobAsync(IJob job, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobExecutor>()
            .ExecuteAsync(job, cancellationToken);
    }
}

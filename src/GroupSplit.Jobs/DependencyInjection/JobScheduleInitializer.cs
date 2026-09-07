using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupSplit.Jobs.DependencyInjection;

internal sealed class JobScheduleInitializer(
    IServiceProvider services, JobSchedulerBuilder builder) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = builder.Registrations;
        if (registrations.Count == 0) return;
        var scheduler = services.GetRequiredService<IJobScheduler>();
        foreach (var (job, schedule) in registrations)
            await scheduler.ScheduleAsync(job, schedule, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

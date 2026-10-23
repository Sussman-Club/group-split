using GroupSplit.API.Services.BackgroundJobs;
using GroupSplit.Jobs;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

public static class BackgroundJobExtensions
{
    /// <summary>Opt-in persistence beside the existing in-memory dispatcher and scheduler.</summary>
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(Registration))) return services;
        services.AddSingleton(new Registration());
        services.AddJobs()
            .Handlers.AddJobHandler<ProcessBackgroundJobs, ProcessBackgroundJobsHandler>()
            .JobsBuilder.Scheduler.Add(new ProcessBackgroundJobs(),
                JobSchedule.Every(TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BackgroundJobRegistry>();
        services.TryAddScoped<IBackgroundJobQueue, BackgroundJobQueue>();
        services.TryAddScoped<IBackgroundJobStore, BackgroundJobStore>();
        services.TryAddScoped<IBackgroundJobProcessor, BackgroundJobProcessor>();
        return services;
    }

    private sealed class Registration;
}

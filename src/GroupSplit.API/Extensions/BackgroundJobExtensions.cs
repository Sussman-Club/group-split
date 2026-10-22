using GroupSplit.API.Services.BackgroundJobs;
using GroupSplit.Jobs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.API.Extensions;

public static class BackgroundJobExtensions
{
    /// <summary>Opt-in persistence beside the existing in-memory dispatcher and scheduler.</summary>
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services)
    {
        services.AddJobs();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BackgroundJobRegistry>();
        services.TryAddScoped<IBackgroundJobQueue, BackgroundJobQueue>();
        services.TryAddScoped<IBackgroundJobStore, BackgroundJobStore>();
        services.AddHostedService<BackgroundJobWorker>();
        return services;
    }
}

using System.Runtime.CompilerServices;
using GroupSplit.Jobs.Defaults;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Jobs;

public static class ServiceExtensions
{
    private static readonly ConditionalWeakTable<IServiceCollection, JobsBuilder> JobsBuilders = [];

    /// <summary>Removes the default transport and worker while preserving custom configuration.</summary>
    public static IJobsBuilder WithoutDefaults(this IJobsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var defaults = builder.Services.Where(service =>
            !service.IsKeyedService &&
            (service.ImplementationType == typeof(DefaultJobQueue) ||
             service.ImplementationType == typeof(DefaultDispatcher) ||
             service.ImplementationType == typeof(DefaultReceiver) ||
             service.ImplementationType == typeof(DefaultJobWorker))).ToArray();

        foreach (var registration in defaults)
            builder.Services.Remove(registration);

        return builder;
    }

    extension(IServiceCollection services)
    {
        public IJobsBuilder AddJobs()
        {
            if (JobsBuilders.TryGetValue(services, out var existingBuilder))
                return existingBuilder;

            var builder = new JobsBuilder(services);
            JobsBuilders.Add(services, builder);

            services.TryAddSingleton<DefaultJobQueue>();
            services.TryAddSingleton<DefaultDispatcher>();
            services.TryAddSingleton<IJobDispatcher>(sp => builder.Dispatcher.BuildDispatcher(sp));
            services.TryAddSingleton<DefaultReceiver>();
            services.TryAddSingleton<IJobReceiver>(sp => builder.Receiver.BuildReceiver(sp));
            services.TryAddScoped<IJobExecutor>(sp => builder.Handlers.BuildExecutor(sp));
            services.AddHostedService<DefaultJobWorker>();

            return builder;
        }
    }
}

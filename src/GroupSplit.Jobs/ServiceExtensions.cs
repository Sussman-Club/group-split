using System.Runtime.CompilerServices;
using GroupSplit.Jobs.Defaults;
using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Jobs;

public static class ServiceExtensions
{
    private static readonly ConditionalWeakTable<IServiceCollection, JobsBuilder> JobsBuilders = [];

    /// <summary>Removes default transport, processing, and scheduling services while preserving custom configuration.</summary>
    public static IJobsBuilder WithoutDefaults(this IJobsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var defaults = builder.Services.Where(service =>
            !service.IsKeyedService &&
            (service.ImplementationType == typeof(DefaultJobQueue) ||
             service.ImplementationType == typeof(DefaultDispatcher) ||
             service.ImplementationType == typeof(DefaultReceiver) ||
             service.ImplementationType == typeof(DefaultJobWorker) ||
             service.ImplementationType == typeof(DefaultJobScheduler) ||
             service.ImplementationType == typeof(DefaultSchedulerWorker) ||
             service.ImplementationType == typeof(JobScheduleInitializer))).ToArray();

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
            services.AddLogging();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<DefaultJobScheduler>();
            services.TryAddSingleton<IJobScheduler>(sp => builder.Scheduler.BuildScheduler(sp));
            services.AddSingleton((JobSchedulerBuilder)builder.Scheduler);
            services.AddHostedService<JobScheduleInitializer>();
            services.AddHostedService<DefaultJobWorker>();
            services.AddHostedService<DefaultSchedulerWorker>();

            return builder;
        }
    }
}

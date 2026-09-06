using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Jobs;

/// <summary>
/// How a host says which jobs exist. Who runs them is a separate project's business.
/// </summary>
/// <remarks>
/// Every host makes these same calls, because a job type and its handler are the same
/// everywhere. What differs is the transport: <c>GroupSplit.Jobs.InProcess</c> adds a
/// queue in memory and a pump; a deployment on a queue service adds that transport's
/// <see cref="IJobQueue"/> and has its function call <see cref="IJobDispatcher"/>. Neither
/// changes a line here.
/// </remarks>
public static class JobServiceExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// The registry and dispatcher, which every host needs whatever moves the messages.
        /// Called for you by the other registrations; call it yourself only when a host
        /// registers no jobs and still dispatches, which is unusual.
        /// </summary>
        public IServiceCollection AddJobs()
        {
            services.AddLogging();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IJobDispatcher, JobDispatcher>();
            services.Registry();
            services.Recurring();

            return services;
        }

        /// <summary>
        /// Declares a job type and the handler that runs it.
        /// </summary>
        public IServiceCollection AddJob<TJob, THandler>()
            where TJob : IJob
            where THandler : class, IJobHandler<TJob>
        {
            services.AddJobs();
            services.Registry().Register<TJob>();
            services.AddScoped<IJobHandler<TJob>, THandler>();

            return services;
        }

        /// <summary>
        /// Asks for <typeparamref name="TJob"/> to be enqueued every
        /// <paramref name="period"/>. The job must already be registered with
        /// <c>AddJob</c>, and must be constructible with nothing, because a schedule has
        /// nothing to tell it.
        /// </summary>
        public IServiceCollection AddRecurringJob<TJob>(TimeSpan period, TimeSpan initialDelay)
            where TJob : IJob, new()
        {
            services.AddJobs();

            services.Recurring().Add(new RecurringJob(
                JobRegistry.NameOf(typeof(TJob)),
                period,
                initialDelay,
                (queue, ct) => queue.EnqueueAsync(new TJob(), ct)));

            return services;
        }

        /// <summary>
        /// The one registry instance, written to during registration and read afterwards.
        /// Held as an instance rather than built from options because <c>AddJob</c> needs to
        /// write to it while the collection is still being assembled.
        /// </summary>
        private JobRegistry Registry() => services.Existing<JobRegistry>();

        private RecurringJobs Recurring() => services.Existing<RecurringJobs>();

        private T Existing<T>() where T : class, new()
        {
            if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(T))
                    ?.ImplementationInstance is T existing)
            {
                return existing;
            }

            var created = new T();
            services.AddSingleton(created);

            return created;
        }
    }
}

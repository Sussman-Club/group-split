using GroupSplit.Jobs.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Jobs.Recurring;

/// <summary>
/// How a host says a job should happen on a period.
/// </summary>
public static class RecurringJobExtensions
{
    extension(IJobsBuilder builder)
    {
        /// <summary>
        /// Asks for <typeparamref name="TJob"/> to be dispatched every
        /// <paramref name="period"/>. The job must have a handler registered on the same
        /// builder, and must be constructible with nothing, because a schedule has nothing
        /// to tell it.
        /// </summary>
        public IJobsBuilder AddRecurringJob<TJob>(TimeSpan period, TimeSpan initialDelay)
            where TJob : IJob, new()
        {
            builder.Services.AddLogging();
            builder.Services.TryAddSingleton(TimeProvider.System);

            Registrations(builder.Services).Add(
                new RecurringJob(typeof(TJob), period, initialDelay, () => new TJob()));

            return builder;
        }
    }

    /// <summary>
    /// The one registration list, written to while the collection is still being assembled
    /// and read once by the scheduler. Held as an instance rather than built from options
    /// for exactly that reason.
    /// </summary>
    private static RecurringJobs Registrations(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(RecurringJobs))
                ?.ImplementationInstance is RecurringJobs existing)
        {
            return existing;
        }

        var created = new RecurringJobs();

        services.AddSingleton(created);
        services.AddHostedService<RecurringJobScheduler>();

        return created;
    }
}

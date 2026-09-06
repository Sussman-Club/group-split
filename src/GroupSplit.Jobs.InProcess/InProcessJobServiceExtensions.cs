using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Jobs.InProcess;

/// <summary>
/// Runs the registered jobs inside this process.
/// </summary>
/// <remarks>
/// The default, and the one deployment shape today: an in-memory queue, a pump draining it
/// into the dispatcher, and a timer for the recurring registrations. A host that would
/// rather have a queue service and a function does not reference this project at all; it
/// registers that transport's <see cref="IJobQueue"/> and calls <see cref="IJobDispatcher"/>
/// from the function, and every <c>AddJob</c> call stays exactly as it was.
/// </remarks>
public static class InProcessJobServiceExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddInProcessJobs()
        {
            services.AddJobs();

            services.TryAddSingleton<InMemoryJobQueue>();
            services.TryAddSingleton<IJobQueue>(provider => provider.GetRequiredService<InMemoryJobQueue>());

            services.AddHostedService<JobPump>();
            services.AddHostedService<RecurringJobScheduler>();

            return services;
        }
    }
}

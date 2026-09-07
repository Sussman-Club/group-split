using Microsoft.Extensions.DependencyInjection;

namespace GroupSplit.Jobs.DependencyInjection;

public interface IJobSchedulerBuilder
    : IJobSchedulerBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>
{
    IJobSchedulerBuilder Use(Func<IServiceProvider, IJobScheduler> factory);
    IJobSchedulerBuilder IJobSchedulerBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>
        .Use(IJobScheduler scheduler) => Use(_ => scheduler);
}

public static class JobSchedulerBuilderExtensions
{
    public static IJobSchedulerBuilder Use<TScheduler>(this IJobSchedulerBuilder builder)
        where TScheduler : class, IJobScheduler =>
        builder.Use(ActivatorUtilities.GetServiceOrCreateInstance<TScheduler>);
}

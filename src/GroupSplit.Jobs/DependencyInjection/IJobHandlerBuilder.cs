using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GroupSplit.Jobs.DependencyInjection;

public interface IJobHandlersBuilder : IJobHandlersBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>
{
    IJobHandlersBuilder Add<TJob>(Func<IServiceProvider, IJobHandler<TJob>> handlerFactory)
        where TJob : IJob;

    IJobHandlersBuilder Add<TJob, TResult>(Func<IServiceProvider, IJobHandler<TJob, TResult>> handlerFactory)
        where TJob : IJob<TResult>;

    IJobHandlersBuilder IJobHandlersBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>.Add<TJob, THandler>(THandler handler) =>
        Add<TJob>(_ => handler);

    IJobHandlersBuilder IJobHandlersBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder, IJobSchedulerBuilder>.Add<TJob, TResult, THandler>(THandler handler) =>
        Add<TJob, TResult>(_ => handler);
}

public static class JobHandlersExtensions
{
    extension(IJobHandlersBuilder builder)
    {
        public IJobHandlersBuilder AddJobHandler<TJob, THandler>()
            where TJob : IJob
            where THandler : class, IJobHandler<TJob>
        {
            builder.JobsBuilder.Services.TryAddTransient<THandler>();

            return builder.Add<TJob>(sp => sp.GetRequiredService<THandler>());
        }

        public IJobHandlersBuilder AddJobHandler<TJob, TResult, THandler>()
            where TJob : IJob<TResult>
            where THandler : class, IJobHandler<TJob, TResult>
        {
            builder.JobsBuilder.Services.TryAddTransient<THandler>();

            return builder.Add<TJob, TResult>(sp => sp.GetRequiredService<THandler>());
        }
    }
}

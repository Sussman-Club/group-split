namespace GroupSplit.Jobs;

public interface IJobHandlersBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder, out TReceiverBuilder, out TSchedulerBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TSchedulerBuilder : IJobSchedulerBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
{
    TJobsBuilder JobsBuilder { get; }

    THandlersBuilder Add<TJob, THandler>(THandler handler)
        where TJob : IJob
        where THandler : IJobHandler<TJob>;

    THandlersBuilder Add<TJob, TResult, THandler>(THandler handler)
        where TJob : IJob<TResult>
        where THandler : IJobHandler<TJob, TResult>;

    internal IJobExecutor BuildExecutor(IServiceProvider serviceProvider);
}

namespace GroupSplit.Jobs;

public interface IJobHandlersBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder, out TReceiverBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
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

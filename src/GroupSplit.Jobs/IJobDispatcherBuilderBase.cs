namespace GroupSplit.Jobs;

public interface IJobDispatcherBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder, out TReceiverBuilder, out TSchedulerBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TSchedulerBuilder : IJobSchedulerBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
{
    TJobsBuilder JobsBuilder { get; }

    TDispatcherBuilder Use(IJobDispatcher dispatcher);

    internal IJobDispatcher BuildDispatcher(IServiceProvider serviceProvider);
}

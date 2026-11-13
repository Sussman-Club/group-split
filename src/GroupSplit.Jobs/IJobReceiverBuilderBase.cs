namespace GroupSplit.Jobs;

public interface IJobReceiverBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder, out TReceiverBuilder, out TSchedulerBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TSchedulerBuilder : IJobSchedulerBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
{
    TJobsBuilder Jobs
    {
        get;
    }

    TReceiverBuilder Use(IJobReceiver receiver);

    internal IJobReceiver BuildReceiver(IServiceProvider serviceProvider);
}

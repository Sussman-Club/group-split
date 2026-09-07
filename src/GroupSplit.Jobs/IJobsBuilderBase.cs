namespace GroupSplit.Jobs;

public interface IJobsBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder, out TReceiverBuilder, out TSchedulerBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TSchedulerBuilder : IJobSchedulerBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
{
    TDispatcherBuilder Dispatcher { get; }
    THandlersBuilder Handlers { get; }
    TReceiverBuilder Receiver { get; }
    TSchedulerBuilder Scheduler { get; }
}

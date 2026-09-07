namespace GroupSplit.Jobs;

public interface IJobDispatcherBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder, out TReceiverBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder>
{
    TJobsBuilder JobsBuilder { get; }

    TDispatcherBuilder Use(IJobDispatcher dispatcher);

    internal IJobDispatcher BuildDispatcher(IServiceProvider serviceProvider);
}

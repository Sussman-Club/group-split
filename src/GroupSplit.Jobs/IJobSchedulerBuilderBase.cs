namespace GroupSplit.Jobs;

public interface IJobSchedulerBuilderBase<out TJobsBuilder, out TDispatcherBuilder, out THandlersBuilder,
    out TReceiverBuilder, out TSchedulerBuilder>
    where TJobsBuilder : IJobsBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TDispatcherBuilder : IJobDispatcherBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where THandlersBuilder : IJobHandlersBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TReceiverBuilder : IJobReceiverBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
    where TSchedulerBuilder : IJobSchedulerBuilderBase<TJobsBuilder, TDispatcherBuilder, THandlersBuilder, TReceiverBuilder, TSchedulerBuilder>
{
    TJobsBuilder JobsBuilder { get; }
    TSchedulerBuilder Use(IJobScheduler scheduler);
    TSchedulerBuilder Add(IJob job, JobSchedule schedule);
    internal IJobScheduler BuildScheduler(IServiceProvider serviceProvider);
}

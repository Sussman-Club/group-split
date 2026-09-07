namespace GroupSplit.Jobs.Standalone;

public sealed class DispatcherBuilder(JobsBuilder jobsBuilder)
    : IJobDispatcherBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder, SchedulerBuilder>
{
    public JobsBuilder JobsBuilder { get; } = jobsBuilder;

    public DispatcherBuilder Use(IJobDispatcher dispatcher)
    {
        JobsBuilder.EnsureMutable();
        ArgumentNullException.ThrowIfNull(dispatcher);
        JobsBuilder.Inner.Dispatcher.Use(_ => new BorrowedDispatcher(dispatcher));
        return this;
    }

    IJobDispatcher IJobDispatcherBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder, SchedulerBuilder>
        .BuildDispatcher(IServiceProvider serviceProvider) => JobsBuilder.Inner.Dispatcher.BuildDispatcher(serviceProvider);

    // The container owns this adapter, not the caller-supplied transport.
    private sealed class BorrowedDispatcher(IJobDispatcher dispatcher) : IJobDispatcher
    {
        public Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken = default) =>
            dispatcher.DispatchAsync(job, cancellationToken);
        public Task<IJobHandle<TResult>> DispatchAsync<TResult>(
            IJob<TResult> job, CancellationToken cancellationToken = default) =>
            dispatcher.DispatchAsync(job, cancellationToken);
    }
}

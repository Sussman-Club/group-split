namespace GroupSplit.Jobs.Standalone;

public sealed class HandlersBuilder(JobsBuilder jobsBuilder)
    : IJobHandlersBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder>
{
    public JobsBuilder JobsBuilder { get; } = jobsBuilder;

    public HandlersBuilder Add<TJob>(IJobHandler<TJob> handler) where TJob : IJob
    {
        JobsBuilder.EnsureMutable();
        ArgumentNullException.ThrowIfNull(handler);
        JobsBuilder.Inner.Handlers.Add<TJob>(_ => handler);
        return this;
    }

    public HandlersBuilder Add<TJob, TResult>(IJobHandler<TJob, TResult> handler) where TJob : IJob<TResult>
    {
        JobsBuilder.EnsureMutable();
        ArgumentNullException.ThrowIfNull(handler);
        JobsBuilder.Inner.Handlers.Add<TJob, TResult>(_ => handler);
        return this;
    }

    public HandlersBuilder Add<TJob, THandler>(THandler handler)
        where TJob : IJob where THandler : IJobHandler<TJob> => Add<TJob>(handler);

    public HandlersBuilder Add<TJob, TResult, THandler>(THandler handler)
        where TJob : IJob<TResult> where THandler : IJobHandler<TJob, TResult> => Add<TJob, TResult>(handler);

    IJobExecutor IJobHandlersBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder>
        .BuildExecutor(IServiceProvider serviceProvider) => JobsBuilder.Inner.Handlers.BuildExecutor(serviceProvider);
}

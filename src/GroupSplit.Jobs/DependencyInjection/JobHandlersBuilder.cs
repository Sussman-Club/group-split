namespace GroupSplit.Jobs.DependencyInjection;

internal class JobHandlersBuilder(IJobsBuilder jobsBuilder) : IJobHandlersBuilder
{
    private readonly Dictionary<Type, JobExecution> _executions = new();

    public IJobsBuilder JobsBuilder => jobsBuilder;

    public IJobHandlersBuilder Add<TJob>(Func<IServiceProvider, IJobHandler<TJob>> handlerFactory)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        if (typeof(TJob).GetInterfaces().Append(typeof(TJob)).Any(
                type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IJob<>)))
            throw new ArgumentException("Register result jobs using Add<TJob, TResult>.", nameof(handlerFactory));

        _executions.Add(typeof(TJob), async (services, job, token) =>
        {
            await handlerFactory(services).HandleAsync((TJob)job, token).ConfigureAwait(false);
            return new VoidResult();
        });
        return this;
    }

    public IJobHandlersBuilder Add<TJob, TResult>(
        Func<IServiceProvider, IJobHandler<TJob, TResult>> handlerFactory)
        where TJob : IJob<TResult>
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        var resultContracts = typeof(TJob).GetInterfaces().Append(typeof(TJob))
            .Count(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IJob<>));
        if (resultContracts > 1)
            throw new ArgumentException(
                $"Job type {typeof(TJob).FullName} declares multiple result contracts. Only one is supported.",
                nameof(handlerFactory));

        _executions.Add(typeof(TJob), async (services, job, token) =>
            await handlerFactory(services).HandleAsync((TJob)job, token).ConfigureAwait(false));
        return this;
    }

    IJobExecutor IJobHandlersBuilderBase<IJobsBuilder, IJobDispatcherBuilder, IJobHandlersBuilder, IJobReceiverBuilder>.BuildExecutor(
        IServiceProvider serviceProvider) => new JobExecutor(serviceProvider, _executions);
}

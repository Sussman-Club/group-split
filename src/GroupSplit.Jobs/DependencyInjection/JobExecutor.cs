namespace GroupSplit.Jobs.DependencyInjection;

internal delegate ValueTask<object?> JobExecution(
    IServiceProvider services, IJob job, CancellationToken cancellationToken);

internal class JobExecutor(
    IServiceProvider serviceProvider,
    IReadOnlyDictionary<Type, JobExecution> executions) : IJobExecutor
{
    public ValueTask<object?> ExecuteAsync(IJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!executions.TryGetValue(job.GetType(), out var execute))
            throw new InvalidOperationException($"No handler registered for job type {job.GetType().FullName}");

        return execute(serviceProvider, job, cancellationToken);
    }
}

namespace GroupSplit.Jobs.Defaults;

internal class DefaultDispatcher(DefaultJobQueue jobQueue) : IJobDispatcher
{
    public async Task<IJobHandle> DispatchAsync(IJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var envelope = new DefaultJobEnvelope(job);
        await jobQueue.JobsChannel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        return new DefaultJobHandle(envelope.Completion);
    }

    public async Task<IJobHandle<TResult>> DispatchAsync<TResult>(IJob<TResult> job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var envelope = new DefaultJobEnvelope(job);
        await jobQueue.JobsChannel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        return new DefaultJobHandle<TResult>(envelope.Completion);
    }
}

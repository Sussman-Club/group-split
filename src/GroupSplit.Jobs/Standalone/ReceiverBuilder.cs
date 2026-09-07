namespace GroupSplit.Jobs.Standalone;

public sealed class ReceiverBuilder(JobsBuilder jobs)
    : IJobReceiverBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder>
{
    public JobsBuilder Jobs { get; } = jobs;

    public ReceiverBuilder Use(IJobReceiver receiver)
    {
        Jobs.EnsureMutable();
        ArgumentNullException.ThrowIfNull(receiver);
        Jobs.Inner.Receiver.Use(_ => new BorrowedReceiver(receiver));
        return this;
    }

    IJobReceiver IJobReceiverBuilderBase<JobsBuilder, DispatcherBuilder, HandlersBuilder, ReceiverBuilder>
        .BuildReceiver(IServiceProvider serviceProvider) => Jobs.Inner.Receiver.BuildReceiver(serviceProvider);

    private sealed class BorrowedReceiver(IJobReceiver receiver) : IJobReceiver
    {
        public IAsyncEnumerable<IJobDelivery> ReceiveAsync(CancellationToken cancellationToken) =>
            receiver.ReceiveAsync(cancellationToken);
    }
}

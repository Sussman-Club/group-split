using System.Runtime.CompilerServices;

namespace GroupSplit.Jobs.Defaults;

internal class DefaultReceiver(DefaultJobQueue jobQueue) : IJobReceiver
{
    public async IAsyncEnumerable<IJobDelivery> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in jobQueue.JobsChannel.Reader.ReadAllAsync(cancellationToken))
                yield return envelope;
        }
        finally
        {
            jobQueue.JobsChannel.Writer.TryComplete();
            while (jobQueue.JobsChannel.Reader.TryRead(out var pending))
                await pending.CancelAsync();
        }
    }
}

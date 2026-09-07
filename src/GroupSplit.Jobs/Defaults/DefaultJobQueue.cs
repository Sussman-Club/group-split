using System.Threading.Channels;

namespace GroupSplit.Jobs.Defaults;

internal class DefaultJobQueue
{
    public Channel<DefaultJobEnvelope> JobsChannel { get; } = Channel.CreateUnbounded<DefaultJobEnvelope>();
}

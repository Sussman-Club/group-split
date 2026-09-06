using System.Threading.Channels;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Connections waiting to be synced, in the order they were asked for.
/// </summary>
/// <remarks>
/// A webhook, "Sync now" and the nightly sweep all put an id here and return; nothing
/// waits on a sync inside a request. <see cref="BankSyncWorker"/> is the one reader. A
/// connection queued twice is synced twice, the second time finding nothing to do, which
/// is cheaper than a set that has to be kept in step with the reader.
/// </remarks>
public sealed class BankSyncQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid connectionId) => _channel.Writer.TryWrite(connectionId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

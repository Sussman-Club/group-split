using System.Threading.Channels;

namespace GroupSplit.Jobs.InProcess;

/// <summary>
/// The default queue: an unbounded channel in this process.
/// </summary>
/// <remarks>
/// Enough for one API instance, which is what runs today. What it does not promise is
/// durability -- a restart loses whatever it held -- and the answer to that is not a
/// bigger in-memory queue but a recurring sweep that re-enqueues anything missed, which is
/// how the bank sync is arranged.
/// <para>
/// Unbounded because the alternative is choosing what to drop, and the volume here is a
/// handful of jobs per person per day. A queue that could genuinely back up wants a
/// transport that can hold it.
/// </para>
/// </remarks>
internal sealed class InMemoryJobQueue(JobRegistry registry, TimeProvider clock) : IJobQueue
{
    private readonly Channel<JobEnvelope> _channel =
        Channel.CreateUnbounded<JobEnvelope>(new UnboundedChannelOptions { SingleReader = true });

    public Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(job);

        _channel.Writer.TryWrite(registry.Envelope(job, clock.GetUtcNow()));

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

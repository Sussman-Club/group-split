using GroupSplit.Jobs;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Pull one connection's changes into staging. What a webhook, "Sync now" and the sweep
/// all ask for.
/// </summary>
/// <remarks>
/// The id and nothing else. A job is a request, not a snapshot: by the time it runs the
/// connection is read afresh, so a status change in between is honoured rather than raced.
/// </remarks>
public sealed record SyncBankConnection(Guid ConnectionId) : IJob;

internal sealed class SyncBankConnectionHandler(IBankSyncService sync) : IJobHandler<SyncBankConnection>
{
    public async ValueTask HandleAsync(SyncBankConnection job, CancellationToken cancellationToken) =>
        await sync.SyncAsync(job.ConnectionId, cancellationToken);
}

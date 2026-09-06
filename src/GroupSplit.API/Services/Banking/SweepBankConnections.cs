using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Ask for every active connection to be synced. Runs once a day.
/// </summary>
/// <remarks>
/// The belt to the webhooks' braces: a webhook the provider could not deliver -- the host
/// was down, the address was wrong for an hour -- costs at most a day. It is a job rather
/// than a timer inside the sync worker so that a deployment which schedules from outside
/// has something to schedule: a daily trigger enqueues this, and this enqueues the rest.
/// <para>
/// It enqueues rather than syncing in place, one job per connection, so a slow bank holds
/// up nobody else's and each connection's failure is its own log line.
/// </para>
/// </remarks>
[JobName("bank.sweep-connections")]
public sealed record SweepBankConnections : IJob;

internal sealed class SweepBankConnectionsHandler(
    AppDbContext dbContext,
    IJobQueue queue,
    ILogger<SweepBankConnectionsHandler> logger) : IJobHandler<SweepBankConnections>
{
    public async Task HandleAsync(SweepBankConnections job, CancellationToken ct = default)
    {
        var ids = await dbContext.Set<BankConnection>()
            .Where(connection => connection.Status == BankConnectionStatus.Active)
            .Select(connection => connection.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
            await queue.EnqueueAsync(new SyncBankConnection(id), ct);

        logger.LogInformation("Bank sweep queued {Count} connections.", ids.Count);
    }
}

using GroupSplit.Data;
using GroupSplit.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Runs the syncs that <see cref="BankSyncQueue"/> holds, and once a day asks for every
/// active connection to be run.
/// </summary>
/// <remarks>
/// Each queued id gets its own scope and its own <c>try</c>, so one connection's failure
/// is one log line and the next id runs. The sweep is a belt to the webhooks' braces: a
/// webhook that never arrived -- the provider could not reach the host, the host was
/// down -- costs at most a day. It waits a little after start so that a restart does not
/// hit the provider for every connection at once, and so that the test hosts, which
/// start this worker like any other, never reach it.
/// </remarks>
public sealed class BankSyncWorker(
    BankSyncQueue queue,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<BankSyncWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan SweepDelay = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan SweepPeriod = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweep = SweepAsync(stoppingToken);

        try
        {
            await foreach (var connectionId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var sync = scope.ServiceProvider.GetRequiredService<IBankSyncService>();

                    await sync.SyncAsync(connectionId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Sync of bank connection {ConnectionId} failed.", connectionId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping, which is the only way out of the loop above.
        }

        await sweep;
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SweepDelay, clock, ct);

            using var timer = new PeriodicTimer(SweepPeriod, clock);

            do
            {
                await EnqueueEveryActiveAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task EnqueueEveryActiveAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var ids = await dbContext.Set<BankConnection>()
                .Where(connection => connection.Status == BankConnectionStatus.Active)
                .Select(connection => connection.Id)
                .ToListAsync(ct);

            foreach (var id in ids)
                queue.Enqueue(id);

            logger.LogInformation("Nightly sweep queued {Count} bank connections.", ids.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "The nightly bank sweep could not list the connections; next try in a day.");
        }
    }
}

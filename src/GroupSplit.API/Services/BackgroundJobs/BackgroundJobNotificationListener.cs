using System.Threading.Channels;
using GroupSplit.Data;
using GroupSplit.Data.PostgreSQL;
using GroupSplit.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Services.BackgroundJobs;

/// <summary>Wakes the existing job executor; never claims or runs durable jobs itself.</summary>
internal sealed class BackgroundJobNotificationListener(
    IServiceScopeFactory scopes, IJobDispatcher dispatcher, TimeProvider clock,
    ILogger<BackgroundJobNotificationListener> logger) : BackgroundService
{
    private readonly Channel<byte> _wakeups = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(ListenAsync(stoppingToken), DispatchAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var retrySeconds = 5;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string connectionString;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
                    connectionString = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
                    {
                        // LISTEN belongs to a session: do not borrow an application pool slot
                        // or enlist the long-lived listener in a caller's transaction.
                        Pooling = false, Enlist = false, KeepAlive = 30, Timeout = 10,
                        CommandTimeout = 15, ApplicationName = "groupsplit-job-wakeup"
                    }.ConnectionString;
                }

                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += (_, args) =>
                {
                    if (args.Channel == BackgroundJobNotifications.Channel) _wakeups.Writer.TryWrite(0);
                };
                await connection.OpenAsync(ct);
                await using (var command = new NpgsqlCommand($"LISTEN {BackgroundJobNotifications.Channel}", connection))
                    await command.ExecuteNonQueryAsync(ct);

                // LISTEN must be committed before scanning. Covers jobs committed before
                // startup or during a disconnect, including the subscribe/scan race.
                _wakeups.Writer.TryWrite(0);
                retrySeconds = 5;
                while (!ct.IsCancellationRequested) await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Connection strings, server details and job payloads stay out of logs.
                logger.LogWarning("Durable job notifications disconnected ({ErrorType}); safety scans remain active.",
                    error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), clock, ct);
                retrySeconds = Math.Min(60, retrySeconds * 2);
            }
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        await foreach (var _ in _wakeups.Reader.ReadAllAsync(ct))
        {
            try
            {
                var handle = await dispatcher.DispatchAsync(new ProcessBackgroundJobs(), ct);
                // Coalesce bursts while the existing worker handles this batch.
                await handle.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("Durable job wakeup dispatch failed ({ErrorType}); safety scans remain active.",
                    error.GetType().Name);
            }
        }
    }
}

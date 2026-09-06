using GroupSplit.API.Services.Banking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GroupSplit.API.Test.Banking;

/// <summary>
/// The worker takes what the queue holds and hands each id to a fresh scope's sync
/// service, and one id's failure does not stop the next.
/// </summary>
public class BankSyncWorkerTest
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_queued_connection_is_synced_and_a_failing_one_does_not_stop_the_next()
    {
        var syncs = new RecordingSyncService();
        var queue = new BankSyncQueue();

        var services = new ServiceCollection()
            .AddSingleton(queue)
            .AddSingleton(TimeProvider.System)
            .AddScoped<IBankSyncService>(_ => syncs)
            .BuildServiceProvider();

        var worker = new BankSyncWorker(queue, services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, NullLogger<BankSyncWorker>.Instance);

        var failing = Guid.NewGuid();
        var fine = Guid.NewGuid();
        syncs.FailOn(failing);

        await worker.StartAsync(Ct);
        try
        {
            queue.Enqueue(failing);
            queue.Enqueue(fine);

            await syncs.Seen(fine).WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal([failing, fine], syncs.Calls);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    private sealed class RecordingSyncService : IBankSyncService
    {
        private readonly Dictionary<Guid, TaskCompletionSource> _seen = new();
        private readonly HashSet<Guid> _failing = [];
        private readonly Lock _lock = new();

        public List<Guid> Calls { get; } = [];

        public void FailOn(Guid connectionId) => _failing.Add(connectionId);

        public Task Seen(Guid connectionId)
        {
            lock (_lock)
                return Source(connectionId).Task;
        }

        public Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                Calls.Add(connectionId);
                Source(connectionId).TrySetResult();
            }

            if (_failing.Contains(connectionId))
                throw new InvalidOperationException("Scripted failure.");

            return Task.FromResult(SyncOutcome.Completed);
        }

        private TaskCompletionSource Source(Guid id)
        {
            if (!_seen.TryGetValue(id, out var source))
                _seen[id] = source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            return source;
        }
    }
}

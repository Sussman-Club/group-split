using System.Collections.Concurrent;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// One sync per connection at a time.
/// </summary>
/// <remarks>
/// A webhook, the nightly sweep and "Sync now" can all name the same connection within a
/// second of each other, and two runs over one cursor would each insert the rows the
/// other had not saved yet. The second caller is told the lock is held and goes away; the
/// run that holds it will see everything the second would have.
/// <para>
/// In process, which is correct for one API instance -- what runs today and what Compose
/// deploys. A second instance needs a database lock, and <see cref="TryHold"/> is the one
/// place to put it. Entries are never removed: there is one per linked bank, and a person
/// links a handful.
/// </para>
/// </remarks>
public sealed class BankSyncLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// The lock for <paramref name="connectionId"/>, to dispose when the run is over, or
    /// null when a run already holds it.
    /// </summary>
    public IDisposable? TryHold(Guid connectionId)
    {
        var semaphore = _locks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));

        return semaphore.Wait(0) ? new Held(semaphore) : null;
    }

    private sealed class Held(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}

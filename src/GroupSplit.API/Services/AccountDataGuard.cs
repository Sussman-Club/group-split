using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GroupSplit.API.Services;

public interface IAccountDataGuard
{
    /// <summary>
    /// Serializes one account's writes across API instances: account deletion, bank linking,
    /// bank sync, receipt uploads and filing, filing, linking and ignoring bank rows, and
    /// settling up.
    /// </summary>
    Task<IAsyncDisposable> Acquire(Guid userId, bool allowDeleted = false, CancellationToken ct = default);

    /// <summary>
    /// The same, also holding the locks of <paramref name="others"/>: the other end of a
    /// repayment, or everybody in a group being settled up. Only <paramref name="userId"/>
    /// is checked for still existing.
    /// </summary>
    Task<IAsyncDisposable> Acquire(Guid userId, IEnumerable<Guid> others, CancellationToken ct = default);
}

/// <summary>
/// A dedicated PostgreSQL session owns the lock across the workflow's multiple saves and
/// external calls. It is never pooled, so disposal (including a failed acquisition) ends
/// the session and releases the lock. No long-lived transaction is relied on.
/// </summary>
/// <remarks>
/// In front of the session, one in-memory gate per account per process. Requests for the
/// same account queue there, and only the one at the front opens a session: without it every
/// waiter held a connection of its own while it waited, and one person's burst -- an agent
/// filing a hundred inbox rows at once -- could use up the server's connections for everybody.
/// The gate is not what makes it correct; the advisory lock is, across instances.
/// <para>
/// Several accounts are locked in a fixed order, gates by id and then database keys by value,
/// so two requests locking overlapping sets cannot each hold what the other waits for. A gate
/// is never waited on while a database lock is held.
/// </para>
/// </remarks>
public sealed class AccountDataGuard(AppDbContext db) : IAccountDataGuard
{
    // One per account this process has locked. Accounts are few; they are not evicted.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public Task<IAsyncDisposable> Acquire(Guid userId, bool allowDeleted = false, CancellationToken ct = default) =>
        Acquire(userId, [userId], allowDeleted, ct);

    public Task<IAsyncDisposable> Acquire(Guid userId, IEnumerable<Guid> others, CancellationToken ct = default) =>
        Acquire(userId, others.Append(userId), allowDeleted: false, ct);

    private async Task<IAsyncDisposable> Acquire(Guid userId, IEnumerable<Guid> accounts, bool allowDeleted,
        CancellationToken ct)
    {
        var ids = accounts.Distinct().Order().ToArray();
        var lease = new Lease();

        try
        {
            foreach (var id in ids)
            {
                var gate = Gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                lease.Gates.Add(gate);
            }

            // EF InMemory test hosts have no database sessions, and the gates alone order
            // their scopes. Production always uses PostgreSQL here.
            if (db.Database.IsRelational())
            {
                var source = (NpgsqlConnection)db.Database.GetDbConnection();
                var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Pooling = false };
                lease.Session = source.CloneWith(settings.ConnectionString);
                await lease.Session.OpenAsync(ct);

                foreach (var key in ids.Select(Key).Distinct().Order())
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", lease.Session)
                        { CommandTimeout = 0 };
                    command.Parameters.AddWithValue("key", key);
                    await command.ExecuteNonQueryAsync(ct);
                }
            }

            // Query scalar database state after waiting, never the middleware's tracked user.
            if (!allowDeleted && !await db.Set<User>().AsNoTracking()
                    .AnyAsync(user => user.Id == userId && user.Kind != UserKind.Deleted, ct))
            {
                throw new NotFoundException(ErrorCodes.AccountNotFound, "This account no longer exists.");
            }

            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private static long Key(Guid userId) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(userId.ToByteArray()));

    private sealed class Lease : IAsyncDisposable
    {
        public List<SemaphoreSlim> Gates { get; } = [];

        public NpgsqlConnection? Session { get; set; }

        public async ValueTask DisposeAsync()
        {
            // The session first: ending it releases the database locks, and only then does
            // the next request in this process go.
            if (Session is not null)
            {
                await Session.DisposeAsync();
                Session = null;
            }

            foreach (var gate in Gates)
            {
                gate.Release();
            }

            Gates.Clear();
        }
    }
}

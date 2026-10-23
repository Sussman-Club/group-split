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
    /// <summary>Serializes account deletion with bank linking and receipt uploads across API instances.</summary>
    Task<IAsyncDisposable> Acquire(Guid userId, bool allowDeleted = false, CancellationToken ct = default);
}

/// <summary>
/// A dedicated PostgreSQL session owns the lock across the workflow's multiple saves and
/// external calls. It is never pooled, so disposal (including a failed acquisition) ends
/// the session and releases the lock. No long-lived transaction or application-only mutex
/// is relied on in production.
/// </summary>
public sealed class AccountDataGuard(AppDbContext db) : IAccountDataGuard
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TestLocks = new();

    public async Task<IAsyncDisposable> Acquire(Guid userId, bool allowDeleted = false, CancellationToken ct = default)
    {
        IAsyncDisposable lease;
        if (db.Database.IsRelational())
        {
            var source = (NpgsqlConnection)db.Database.GetDbConnection();
            var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Pooling = false };
            var connection = source.CloneWith(settings.ConnectionString);
            try
            {
                await connection.OpenAsync(ct);
                var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(userId.ToByteArray()));
                await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection) { CommandTimeout = 0 };
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteNonQueryAsync(ct);
                lease = connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        else
        {
            // EF InMemory test hosts have no database sessions. Match the same ordering
            // between their independent scopes; production always uses PostgreSQL above.
            var gate = TestLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            lease = new TestLease(gate);
        }

        try
        {
            // Query scalar database state after waiting, never the middleware's tracked user.
            if (!allowDeleted && !await db.Set<User>().AsNoTracking()
                    .AnyAsync(user => user.Id == userId && user.Kind != UserKind.Deleted, ct))
                throw new NotFoundException(ErrorCodes.AccountNotFound, "This account no longer exists.");
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class TestLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

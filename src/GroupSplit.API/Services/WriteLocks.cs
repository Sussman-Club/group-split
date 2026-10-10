using GroupSplit.Data;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Making a check and the write it decides one thing, for the writes a double submit races.
/// </summary>
/// <remarks>
/// A check-then-write in two statements is two requests' worth of checks before either
/// writes: both find the row still waiting, both find the balance still owed, and both act
/// on it. Each write that decides something from what it reads takes a row lock first, in
/// a transaction that lasts until the write is committed, so the second request's read
/// waits for the first one's answer rather than racing it.
/// <para>
/// Locks rather than a stricter isolation level, because the second request should be told
/// what happened -- "already filed", "nothing to settle" -- and a lock lets it read that and
/// say so, where a serialization failure is a retry that ends the same way after more work.
/// </para>
/// <para>
/// Nothing at all on a provider without transactions or row locks. The unit suite's
/// in-memory database runs one request at a time, and has nothing to race.
/// </para>
/// </remarks>
internal static class WriteLocks
{
    extension(AppDbContext context)
    {
        /// <summary>
        /// Runs <paramref name="work"/> in one database transaction, committed when it
        /// returns -- or inside the transaction already open, which then commits it.
        /// </summary>
        /// <remarks>
        /// Retried as a whole on a transient failure, as the deployed provider does with
        /// every save, starting over from the database; see <c>CallerScope.StartOver</c>.
        /// </remarks>
        public async Task<T> Atomically<T>(ICurrentUser caller, Func<Task<T>> work, CancellationToken ct)
        {
            if (!context.Database.IsRelational() || context.Database.CurrentTransaction is not null)
                return await work();

            var retrying = false;

            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                if (retrying)
                    context.StartOver(caller.User);

                retrying = true;

                await using var transaction = await context.Database.BeginTransactionAsync(ct);

                var result = await work();

                await transaction.CommitAsync(ct);

                return result;
            });
        }

        public Task Atomically(ICurrentUser caller, Func<Task> work, CancellationToken ct) =>
            context.Atomically(caller, async () =>
            {
                await work();
                return true;
            }, ct);

        /// <summary>
        /// Holds the groups against anything else that decides from their balances or their
        /// members -- settling up, leaving, removing somebody -- until the transaction ends.
        /// </summary>
        /// <remarks>
        /// <c>FOR NO KEY UPDATE</c>: exclusive among those, and against
        /// <see cref="ShareGroups"/>, but not against the key-share lock every insert that
        /// points at the group takes. Locked in id order, so two callers holding overlapping
        /// sets queue rather than deadlock.
        /// </remarks>
        public async Task LockGroups(IReadOnlyCollection<Guid> groupIds, CancellationToken ct)
        {
            if (!InTransaction(context) || groupIds.Count == 0)
                return;

            var ids = groupIds.Distinct().ToArray();

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Group" WHERE "Id" = ANY({ids}) ORDER BY "Id" FOR NO KEY UPDATE""", ct);
        }

        /// <summary>
        /// Holds the groups against <see cref="LockGroups"/> while money is recorded, changed
        /// or deleted in them, so a member cannot leave between this request reading who is
        /// in the group and its write naming them. Shared: expenses do not wait for each
        /// other.
        /// </summary>
        /// <remarks>
        /// Nulls are skipped, so a personal expense -- or the personal end of a move -- can
        /// be passed as it is. In id order, like <see cref="LockGroups"/>, so a move holding
        /// two groups queues behind a settle-up holding the same two rather than deadlocking.
        /// </remarks>
        public async Task ShareGroups(IEnumerable<Guid?> groupIds, CancellationToken ct)
        {
            if (!InTransaction(context))
                return;

            var ids = groupIds.OfType<Guid>().Distinct().ToArray();

            if (ids.Length == 0)
                return;

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Group" WHERE "Id" = ANY({ids}) ORDER BY "Id" FOR SHARE""", ct);
        }

        /// <summary>
        /// Claims an expense for this request's linking until the transaction ends, so two
        /// imported rows linked to it at once are one link and one refusal rather than one
        /// link overwriting the other and leaving its row filed against nothing.
        /// </summary>
        public async Task LockTransaction(Guid id, CancellationToken ct)
        {
            if (!InTransaction(context))
                return;

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Transaction" WHERE "Id" = {id} FOR NO KEY UPDATE""", ct);
        }

        /// <summary>
        /// Claims an imported row for this request's filing or linking until the transaction
        /// ends. Read the row after this, not before: the read then sees whatever the request
        /// that held it last committed.
        /// </summary>
        public async Task LockBankTransaction(Guid id, CancellationToken ct)
        {
            if (!InTransaction(context))
                return;

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "BankTransaction" WHERE "Id" = {id} FOR NO KEY UPDATE""", ct);
        }
    }

    /// <summary>
    /// Whether a lock taken now would last. Outside a transaction it would be released by
    /// the statement that took it, which is a caller's mistake, not something to shrug at.
    /// </summary>
    private static bool InTransaction(AppDbContext context)
    {
        if (!context.Database.IsRelational())
            return false;

        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A row lock only lasts inside a transaction; take it inside Atomically.");

        return true;
    }
}

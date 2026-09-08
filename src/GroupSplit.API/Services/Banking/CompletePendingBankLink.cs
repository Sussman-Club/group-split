using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Finish one link that the request which started it did not get to store.
/// </summary>
/// <remarks>
/// The id and nothing else, for the same reason a sync carries only a connection id: by the
/// time this runs the row is read afresh, so a link the original request went on to finish
/// after all is a no-op rather than a race.
/// </remarks>
public sealed record CompletePendingBankLink(Guid PendingLinkId) : IJob;

internal sealed class CompletePendingBankLinkHandler(IBankConnectionService connections)
    : IJobHandler<CompletePendingBankLink>
{
    public async ValueTask HandleAsync(CompletePendingBankLink job, CancellationToken cancellationToken) =>
        await connections.CompletePending(job.PendingLinkId, cancellationToken);
}

/// <summary>
/// Give up on one link that has been tried enough times, handing back the access it holds.
/// </summary>
/// <remarks>
/// A job of its own rather than something the sweep does inline, because giving up talks to
/// the provider: it belongs on the same queue, with the same retries and the same isolation
/// from the sweep, as the finishing it is the counterpart to.
/// </remarks>
public sealed record AbandonPendingBankLink(Guid PendingLinkId) : IJob;

internal sealed class AbandonPendingBankLinkHandler(IBankConnectionService connections)
    : IJobHandler<AbandonPendingBankLink>
{
    public async ValueTask HandleAsync(AbandonPendingBankLink job, CancellationToken cancellationToken) =>
        await connections.AbandonPending(job.PendingLinkId, cancellationToken);
}

/// <summary>
/// Ask for every interrupted link still worth finishing to be finished.
/// </summary>
/// <remarks>
/// The counterpart to the bank sweep, and the thing that makes writing an interrupted link
/// down worth anything: without something coming back for these rows they would be a record
/// of a loss rather than a way out of one.
/// <para>
/// Links younger than <see cref="Grace"/> are left alone, because a request that is merely
/// slow is indistinguishable here from one that died, and finishing a link underneath a
/// request that is still working on it wins nothing -- storing is idempotent, but the
/// wasted work and the confusing log are avoidable by waiting.
/// </para>
/// <para>
/// Attempts are bounded. A link that cannot be finished -- an expired public token, an item
/// whose stored form will not read -- stops being retried and is given up on, which means
/// handing its access back to the provider and dropping the row rather than leaving it.
/// Leaving it was the other option and it is the worse one: the row holds a working bank
/// access token, nothing surfaces it, nothing revisits it, and the item it names belongs to
/// no connection anybody can see. Handing the access back is the only thing that actually
/// resolves that, and where the provider will not take it the log line says so by name.
/// </para>
/// </remarks>
public sealed record SweepPendingBankLinks : IJob;

internal sealed class SweepPendingBankLinksHandler(
    AppDbContext dbContext,
    IJobDispatcher jobs,
    TimeProvider clock,
    ILogger<SweepPendingBankLinksHandler> logger) : IJobHandler<SweepPendingBankLinks>
{
    /// <summary>How long a link is left to the request that started it.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many times finishing is tried before the row is left alone. Generous, because
    /// each attempt is cheap and the thing being protected does not come back.
    /// </summary>
    internal const int MaxAttempts = 5;

    public async ValueTask HandleAsync(SweepPendingBankLinks job, CancellationToken cancellationToken)
    {
        var startedBefore = clock.GetUtcNow() - Grace;

        // One read, split here. Two reads over the same predicate would be the faster shape
        // and it is the wrong one: a row whose Attempts crossed MaxAttempts between them
        // comes back from both, and this would then ask for it to be finished and given up
        // on at once. Neither of those re-checks the count, so under a job transport that
        // runs them side by side the give-up retires the item at the provider while the
        // finish is still storing a connection that names it.
        //
        // Reading both halves at once costs nothing here: rows are given up on rather than
        // left, so this table holds only what is genuinely in flight.
        var links = await dbContext.Set<PendingBankLink>()
            .Where(link => link.StartedAt <= startedBefore)
            .Select(link => new { link.Id, link.Attempts })
            .ToListAsync(cancellationToken);

        var worthTrying = links.Where(link => link.Attempts < MaxAttempts).Select(link => link.Id).ToList();
        var giveUpOn = links.Where(link => link.Attempts >= MaxAttempts).Select(link => link.Id).ToList();

        foreach (var id in worthTrying)
        {
            await jobs.DispatchAsync(new CompletePendingBankLink(id), cancellationToken);
        }

        foreach (var id in giveUpOn)
        {
            await jobs.DispatchAsync(new AbandonPendingBankLink(id), cancellationToken);
        }

        if (worthTrying.Count > 0)
        {
            logger.LogInformation("Queued {Count} interrupted bank links to finish.", worthTrying.Count);
        }

        if (giveUpOn.Count > 0)
        {
            // Worth an error rather than a note: each one was an item live at the provider
            // that this application never managed to attach to anybody.
            logger.LogError(
                "{Count} interrupted bank links have been tried {MaxAttempts} times and are being given up "
                + "on. Each held a provider item that belongs to no connection here, and is being handed "
                + "back.",
                giveUpOn.Count, MaxAttempts);
        }
    }
}

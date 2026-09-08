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
/// whose stored form will not read -- stops being retried and stays as a row, because it is
/// the only remaining record that an item exists at the provider under this account. Those
/// are counted in the log rather than deleted.
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

        var links = await dbContext.Set<PendingBankLink>()
            .Where(link => link.StartedAt <= startedBefore)
            .Select(link => new { link.Id, link.Attempts })
            .ToListAsync(cancellationToken);

        var (worthTrying, abandoned) = (
            links.Where(link => link.Attempts < MaxAttempts).ToList(),
            links.Count(link => link.Attempts >= MaxAttempts));

        foreach (var link in worthTrying)
        {
            await jobs.DispatchAsync(new CompletePendingBankLink(link.Id), cancellationToken);
        }

        if (worthTrying.Count > 0)
        {
            logger.LogInformation("Queued {Count} interrupted bank links to finish.", worthTrying.Count);
        }

        if (abandoned > 0)
        {
            // Worth an error rather than a note: each one is an item live at the provider
            // that this application has given up on attaching to anybody.
            logger.LogError(
                "{Count} interrupted bank links have been tried {MaxAttempts} times and are being left. "
                + "Each is a provider item that exists and belongs to no connection here.",
                abandoned, MaxAttempts);
        }
    }
}

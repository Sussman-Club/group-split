using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Jobs;
using GroupSplit.Keycloak;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Deletes the Keycloak login of an account that has already been deleted here.
/// </summary>
/// <remarks>
/// The second half of deleting an account, and the half in another system. What is owed is
/// written down first -- a <see cref="PendingLoginDeletion"/>, saved with the anonymisation --
/// so that this can fail without the deletion being lost: the account stays deleted, the row
/// stays, and <see cref="SweepPendingLoginDeletions"/> comes back for it.
/// </remarks>
public interface ILoginDeletion
{
    /// <summary>
    /// Tries once to delete the login a pending deletion names, and reports whether it is gone.
    /// A failure is recorded against the row and logged, never thrown: the deletion it belongs
    /// to has already happened, and the sweep is what tries again.
    /// </summary>
    Task<bool> TryDeleteAsync(Guid pendingId, CancellationToken cancellationToken = default);
}

internal sealed class LoginDeletion(
    AppDbContext context,
    IKeycloakUserRemoval keycloak,
    ILogger<LoginDeletion> logger) : ILoginDeletion
{
    /// <summary>
    /// After this many failures each further one is logged as an error rather than a warning.
    /// It never stops being retried: giving up would leave a login standing for somebody who
    /// asked for it to be deleted, and the only thing it costs to keep trying is a request.
    /// </summary>
    internal const int AttemptsBeforeError = 3;

    public async Task<bool> TryDeleteAsync(Guid pendingId, CancellationToken cancellationToken = default)
    {
        var pending = await context.Set<PendingLoginDeletion>()
            .FirstOrDefaultAsync(candidate => candidate.Id == pendingId, cancellationToken);

        // Somebody else finished it -- the request that wrote it, or a sweep that overlapped.
        if (pending is null) return true;

        try
        {
            await keycloak.RemoveUserAsync(pending.IdentityId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            pending.Attempts++;
            await context.SaveChangesAsync(cancellationToken);

            // Neither level is a failure of the deletion, which has happened. It is a login
            // that should not exist and still does, and the person who asked for it to go is
            // refused at sign-in until it does.
            logger.Log(
                pending.Attempts >= AttemptsBeforeError ? LogLevel.Error : LogLevel.Warning,
                exception,
                "Could not delete the Keycloak login {IdentityId} of an account deleted at {RequestedAt} "
                + "(attempt {Attempts}). The account is deleted and its sign-in is refused; deleting the "
                + "login will be retried.",
                pending.IdentityId, pending.RequestedAt, pending.Attempts);

            return false;
        }

        context.Remove(pending);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Removed under us by an overlapping attempt, which also deleted the login.
            context.ChangeTracker.Clear();
        }

        logger.LogInformation("Deleted the Keycloak login {IdentityId} of an account deleted at {RequestedAt}.",
            pending.IdentityId, pending.RequestedAt);

        return true;
    }
}

/// <summary>
/// Try again to delete every login that is still owed. Runs every five minutes.
/// </summary>
/// <remarks>
/// <para>
/// Every five minutes rather than daily, for the same reason interrupted bank links are: a
/// person is involved. Their account is deleted and their login is not, so an outage at
/// Keycloak should cost them minutes of a login that still exists, not a day.
/// </para>
/// <para>
/// Rows younger than <see cref="Grace"/> are left to the request that wrote them, which tries
/// straight after saving. Overlapping it would be harmless -- deleting a login that is already
/// gone succeeds -- but it would log the same deletion twice.
/// </para>
/// </remarks>
public sealed record SweepPendingLoginDeletions : IJob;

internal sealed class SweepPendingLoginDeletionsHandler(
    AppDbContext context,
    ILoginDeletion logins,
    TimeProvider clock,
    ILogger<SweepPendingLoginDeletionsHandler> logger) : IJobHandler<SweepPendingLoginDeletions>
{
    internal static readonly JobSchedule.CronSchedule Schedule = JobSchedule.Cron("*/5 * * * *", TimeZoneInfo.Utc);

    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    public async ValueTask HandleAsync(SweepPendingLoginDeletions job, CancellationToken cancellationToken)
    {
        var requestedBefore = clock.GetUtcNow() - Grace;

        var ids = await context.Set<PendingLoginDeletion>()
            .Where(pending => pending.RequestedAt <= requestedBefore)
            .OrderBy(pending => pending.RequestedAt)
            .Select(pending => pending.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0) return;

        var deleted = 0;

        // One at a time. These are rare -- each is an account deletion Keycloak was not there
        // for -- and when there are several, Keycloak being down is the likely reason for all
        // of them at once.
        foreach (var id in ids)
        {
            if (await logins.TryDeleteAsync(id, cancellationToken)) deleted++;
        }

        logger.LogInformation("Deleted {Deleted} of {Count} Keycloak logins still owed for deleted accounts.",
            deleted, ids.Count);
    }
}

using GroupSplit.Data;
using GroupSplit.API.Errors;
using GroupSplit.Shared.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

public interface IAccountService
{
    /// <summary>
    /// Deletes one account, reporting the groups that block it.
    /// </summary>
    /// <returns>
    /// The groups where that account's balance is not settled. Empty when the account
    /// was deleted; nothing is changed when it is not.
    /// </returns>
    /// <remarks>
    /// Deletes whichever account it is handed and asks nothing about who is asking:
    /// deciding that is the caller's job. <c>DELETE /users/me</c> passes the
    /// authenticated user's own id, which is what keeps it self-service.
    /// </remarks>
    Task<IReadOnlyList<OutstandingBalance>> DeleteAccount(Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class AccountService(
    ICurrentUser userContext,
    IGroupService groups,
    ILoginDeletion logins,
    TimeProvider clock,
    AppDbContext context,
    ILogger<AccountService> logger) : IAccountService
{
    /// <summary>
    /// How long the request waits for Keycloak before leaving the login to the sweep. Short,
    /// because somebody is watching a button, and because nothing is lost by giving up: the
    /// account is already deleted and the login deletion is written down.
    /// </summary>
    internal static readonly TimeSpan LoginDeletionWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The account is anonymised rather than erased. Its transactions and settlements are
    /// half of somebody else's history -- one member's expense is what every other
    /// member's share was calculated from -- so deleting the rows would silently rewrite
    /// the ledger for people who did not ask for it. What goes is everything that
    /// identifies the person: their name, their address, and their login, which is deleted
    /// from Keycloak along with every session it holds. See <c>docs/account-deletion.md</c>.
    /// </summary>
    public async Task<IReadOnlyList<OutstandingBalance>> DeleteAccount(Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Leaving every group at once, so holding every group the way leaving one does: an
        // expense naming them cannot land between their balances being read and the save.
        var (outstanding, pending) = await context.Atomically(userContext, async () =>
        {
            await context.LockGroups(await context.Set<GroupMembership>()
                .Where(membership => membership.UserId == userId)
                .Select(membership => membership.GroupId)
                .ToListAsync(cancellationToken), cancellationToken);

            return await Deleted(userId, cancellationToken);
        }, cancellationToken);

        // Once the deletion is committed, and outside the transaction: Keycloak is another
        // system, a login deleted there cannot be rolled back with a failed commit here, and
        // the groups should not stay locked while it answers.
        if (pending is not null)
        {
            await DeleteLoginNow(pending, cancellationToken);
        }

        return outstanding;
    }

    /// <summary>
    /// The deletion itself, saved but not yet committed, and the login deletion it now owes
    /// Keycloak, if any.
    /// </summary>
    private async Task<(IReadOnlyList<OutstandingBalance> Outstanding, PendingLoginDeletion? Pending)> Deleted(
        Guid userId, CancellationToken cancellationToken)
    {
        var user = await context.Set<User>()
                       .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
                   ?? throw new NotFoundException(ErrorCodes.AccountNotFound, $"No account with id {userId}.");

        // Already done, login deletion included: that was written down the first time and is
        // the sweep's to finish. Only reachable now that a caller can name an account rather
        // than be one -- a deleted account cannot sign in to ask twice.
        if (user.Kind == UserKind.Deleted)
        {
            return ([], null);
        }

        await context.Entry(user).Collection(entity => entity.Groups)
            .LoadAsync(cancellationToken);

        // Every group they are in is a shared one now. The hidden "Personal" group that
        // used to be among them -- and had to be filtered out here, since it holds only
        // their own records and blocks nothing -- is gone: a personal expense is one with
        // no group at all.
        var sharedGroups = user.Groups.ToList();

        // Every group is checked before any of them is touched. Settling up first is the
        // same rule that already governs leaving a single group, and applying it group by
        // group as we went would leave an account half-deleted on the first debt found:
        // out of the groups already processed, still in the rest.
        var outstanding = new List<OutstandingBalance>();

        foreach (var group in sharedGroups)
        {
            // Scoped to this account, not to whoever is calling. The caller need not share
            // the group -- and when they do not, the caller-scoped query would return no
            // rows, which is indistinguishable from a settled balance.
            var balance = await (await groups.GetGroupNetBalanceFor(group.Id, user.Id, cancellationToken))
                .Where(netBalance => netBalance.UserId == user.Id)
                .Select(netBalance => netBalance.Balance)
                .FirstOrDefaultAsync(cancellationToken);

            if (balance is not 0)
            {
                outstanding.Add(new OutstandingBalance(group.Id, group.Name, balance));
            }
        }

        if (outstanding.Count > 0)
        {
            return (outstanding, null);
        }

        foreach (var group in sharedGroups)
        {
            await groups.DetachMember(group, user, LeftReason.AccountDeleted, user.Id, cancellationToken);
        }

        // Nothing to do about invitations sent *to* them: an invitation names a person the
        // group made up and holds a stand-in of its own, and claiming one is what attaches a
        // real account to it -- which deletes both. So no pending invitation can name this
        // account, and there is no address left for one to match. The invitations they
        // *sent* keep pointing at them until the reference is cleared, which is the FK's own
        // doing.

        // The login is deleted from Keycloak, and that is another system: it cannot be part of
        // the save below, so what is owed to it is. The identity row stays, as a tombstone --
        // UserProvisioner refuses a sign-in that finds it on a deleted account, which covers
        // the moments before Keycloak catches up and any token issued before it did.
        await context.Entry(user).Reference(entity => entity.Identity)
            .LoadAsync(cancellationToken);

        // Absent for a stand-in a group named, which has no login to delete.
        var pending = user.Identity is { } identity
            ? new PendingLoginDeletion { IdentityId = identity.IdentityId, RequestedAt = clock.GetUtcNow() }
            : null;

        if (pending is not null)
        {
            context.Add(pending);
        }

        user.FirstName = null;
        user.LastName = null;
        user.Email = null;
        user.Kind = UserKind.Deleted;

        // One save for the whole thing, so a failure part way through leaves the account
        // as it was rather than stranded between states -- and the login deletion is never
        // owed without the account having gone, or the other way round.
        await context.SaveChangesAsync(cancellationToken);

        return (outstanding, pending);
    }

    /// <summary>
    /// The first attempt, inside the request, so that in the ordinary case the login is gone
    /// -- and its sessions with it -- by the time the person is told their account is.
    /// Bounded, and never a failure of the request: the account is deleted whatever happens
    /// here, and anything left over is the sweep's.
    /// </summary>
    private async Task DeleteLoginNow(PendingLoginDeletion pending, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(LoginDeletionWait);

        try
        {
            await logins.TryDeleteAsync(pending.Id, wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Keycloak did not answer within {Wait} when deleting the login {IdentityId}. The account is "
                + "deleted and its sign-in is refused; deleting the login will be retried.",
                LoginDeletionWait, pending.IdentityId);
        }
    }
}

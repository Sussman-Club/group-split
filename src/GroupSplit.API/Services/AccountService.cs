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
    IGroupService groups,
    AppDbContext context) : IAccountService
{
    /// <summary>
    /// The account is anonymised rather than erased. Its transactions and settlements are
    /// half of somebody else's history -- one member's expense is what every other
    /// member's share was calculated from -- so deleting the rows would silently rewrite
    /// the ledger for people who did not ask for it. What goes is everything that
    /// identifies the person: their name, their address, and the mapping to their
    /// Keycloak subject.
    /// </summary>
    public async Task<IReadOnlyList<OutstandingBalance>> DeleteAccount(Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await context.Set<User>()
                       .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
                   ?? throw new NotFoundException(ErrorCodes.AccountNotFound, $"No account with id {userId}.");

        await context.Entry(user).Reference(entity => entity.PersonalGroup)
            .LoadAsync(cancellationToken);
        await context.Entry(user).Collection(entity => entity.Groups)
            .LoadAsync(cancellationToken);

        // The personal group holds only their own records and nobody else can see it, so
        // it never blocks anything and there is no membership to hand back.
        var sharedGroups = user.Groups
            .Where(group => group.Id != user.PersonalGroup.Id)
            .ToList();

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
            return outstanding;
        }

        foreach (var group in sharedGroups)
        {
            await groups.DetachMember(group, user, cancellationToken);
        }

        // Keycloak keeps the login -- deleting it there is a separate decision -- so
        // dropping the mapping is what stops a later sign-in resurrecting this account.
        // Without it the next request would find the row by subject and hand back the
        // profile that was just cleared.
        await context.Entry(user).Reference(entity => entity.Identity)
            .LoadAsync(cancellationToken);

        // Absent when the account has already been anonymised. Reaching that state twice
        // is only possible now that a caller can name an account instead of being one.
        if (user.Identity is not null)
        {
            context.Remove(user.Identity);
        }

        user.FirstName = null;
        user.LastName = null;
        user.Email = null;

        // One save for the whole thing, so a failure part way through leaves the account
        // as it was rather than stranded between states.
        await context.SaveChangesAsync(cancellationToken);

        return outstanding;
    }
}

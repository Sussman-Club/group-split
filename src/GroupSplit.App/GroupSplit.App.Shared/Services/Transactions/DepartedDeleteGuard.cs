using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Transactions;

/// <summary>
/// Whether an expense or settlement holds somebody who has left its group -- the one thing
/// that makes deleting it a request the server will refuse.
/// </summary>
/// <remarks>
/// Asked before the delete confirmation rather than learned from the refusal after it. The
/// server is still the judge: it refuses with <c>TRANSACTION_MOVES_DEPARTED_BALANCE</c>
/// whatever this says, and covers cases this cannot see. This only spares somebody a
/// "Delete?" whose every answer ends in "could not delete".
/// </remarks>
public interface IDepartedDeleteGuard
{
    /// <summary>
    /// The name of the first person on the transaction -- payer, or holder of a share that
    /// is not zero -- who is no longer in its group. Null when everybody is, when it has no
    /// group, or when either read fails and the server is left to decide.
    /// </summary>
    /// <param name="groupId">
    /// The group the row says it is in, where the caller has it. Lets the roster be read
    /// beside the transaction rather than after it, and a personal expense -- which has no
    /// group to have left -- be answered without reading anything.
    /// </param>
    Task<string?> FirstLeaverOnAsync(Guid transactionId, Guid? groupId, CancellationToken ct = default);
}

public sealed class DepartedDeleteGuard(ITransactionsClient transactions, IGroupsClient groups)
    : IDepartedDeleteGuard
{
    public async Task<string?> FirstLeaverOnAsync(Guid transactionId, Guid? groupId,
        CancellationToken ct = default)
    {
        if (groupId is not { } expected)
            return null;

        try
        {
            // Both at once: the roster does not depend on the answer about the transaction,
            // only on which group it is in, and the row already says that.
            var reading = transactions.GetTransactionAsync(transactionId, ct);
            var rostering = groups.GetGroupMembersAsync(expected, ct);

            // Together, so a failure of either is caught below rather than left unobserved.
            await Task.WhenAll(reading, rostering);

            var details = await reading;

            if (details.GroupId is not { } groupOf)
                return null;

            // Moved since the row was read: ask about the group it is in now.
            var people = groupOf == expected
                ? await rostering
                : await groups.GetGroupMembersAsync(groupOf, ct);

            // Members and invitees alike: an invitee holds shares and has not left, so the
            // roster that lists both is the one to compare against.
            var roster = people.Select(person => person.Id).ToHashSet();

            if (!roster.Contains(details.PaidByUserId))
                return details.PaidByUserName;

            // A share of nothing is no position, and the server lets such an entry go; so
            // must this, or the page refuses a delete the API would make.
            return details.Splits
                .FirstOrDefault(split => split.Amount != 0m && !roster.Contains(split.UserId))?
                .UserName;
        }
        catch (ApiException)
        {
            return null;
        }
    }
}

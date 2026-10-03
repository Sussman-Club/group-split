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
    /// The name of the first person on the transaction -- payer or share-holder -- who is no
    /// longer in its group, or null when everybody is, when it has no group, or when either
    /// read fails and the server is left to decide.
    /// </summary>
    Task<string?> FirstLeaverOnAsync(Guid transactionId, CancellationToken ct = default);
}

public sealed class DepartedDeleteGuard(ITransactionsClient transactions, IGroupsClient groups)
    : IDepartedDeleteGuard
{
    public async Task<string?> FirstLeaverOnAsync(Guid transactionId, CancellationToken ct = default)
    {
        try
        {
            var details = await transactions.GetTransactionAsync(transactionId, ct);

            if (details.GroupId is not { } groupId)
                return null;

            // Members and invitees alike: an invitee holds shares and has not left, so the
            // roster that lists both is the one to compare against.
            var roster = (await groups.GetGroupMembersAsync(groupId, ct))
                .Select(person => person.Id)
                .ToHashSet();

            if (!roster.Contains(details.PaidByUserId))
                return details.PaidByUserName;

            return details.Splits.FirstOrDefault(split => !roster.Contains(split.UserId))?.UserName;
        }
        catch (ApiException)
        {
            return null;
        }
    }
}

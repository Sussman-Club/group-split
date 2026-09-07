using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Settling your own position in a group up, in one action.
/// </summary>
/// <remarks>
/// The Settle button records one repayment between the caller and one other member. This
/// records all of them at once: the same transfers, decided the same way, written together
/// instead of one dialog at a time.
/// <para>
/// Every payment it writes has the caller on one end of it. That is the whole shape of this:
/// a member can say what they paid and what they were paid, because both are things they
/// were there for, and cannot say that two other members squared up between themselves. A
/// group-wide version would have one person recording money moving between two others on
/// their behalf.
/// </para>
/// <para>
/// It settles the caller's whole outstanding position and nothing narrower, which is what
/// lets it keep no bookkeeping of its own. Balances are cumulative and self-correcting: pay
/// what the balance says, and it goes to zero. Nothing has to remember that this happened,
/// because a second settling-up over a position already at zero has nothing left to write.
/// </para>
/// </remarks>
public interface ISettlementService
{
    /// <summary>
    /// Records every repayment between the caller and the rest of the group, in one go.
    /// </summary>
    Task<SettleUpResponse> SettleUp(Guid groupId, SettleUpRequest request,
        CancellationToken cancellationToken = default);
}

public class SettlementService(
    ICurrentUser userContext,
    IGroupService groups,
    IDebtCalculationService debtCalculator,
    AppDbContext context) : ISettlementService
{
    public async Task<SettleUpResponse> SettleUp(Guid groupId, SettleUpRequest request,
        CancellationToken cancellationToken = default)
    {
        // Scoped to the caller's own groups, so a group they are not in is not found rather
        // than empty -- an empty answer here would read as "already settled".
        var group = await (await groups.GetGroupById(groupId, cancellationToken))
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        var balances = await (await groups.GetGroupNetBalance(groupId, cancellationToken))
            .ToArrayAsync(cancellationToken);

        // The same calculation the group's balances view shows, and deliberately not the
        // amounts a client might send: between somebody opening the dialog and pressing the
        // button, another expense can land.
        var position = await debtCalculator.GetUserBalance(balances);

        var payments = position.Payments(userContext.User.Id);

        if (payments.Count is 0)
            throw new ConflictException(ErrorCodes.SettlementNothingToSettle,
                "You are already square with everybody in this group.");

        var date = (request.Date ?? DateTimeOffset.UtcNow).ToUniversalTime();

        var description = request.Description?.Trim() is { Length: > 0 } note ? note : null;

        var members = await MembersIn(payments, cancellationToken);

        foreach (var payment in payments)
        {
            context.Add(Transfer.Between(group, members[payment.FromUserId], members[payment.ToUserId],
                payment.Amount, date, description));
        }

        // One save. Half a settling-up would leave somebody looking at a balance that had
        // moved for some of the people in it and not the others, with no way to tell which.
        await context.SaveChangesAsync(cancellationToken);

        return new SettleUpResponse
        {
            Payments = payments,
            Date = date,
            Description = description
        };
    }

    private async Task<Dictionary<Guid, User>> MembersIn(IReadOnlyList<SettlementPayment> payments,
        CancellationToken cancellationToken)
    {
        var ids = payments
            .SelectMany(payment => new[] { payment.FromUserId, payment.ToUserId })
            .Distinct()
            .ToList();

        return await context.Set<User>()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);
    }
}

file static class CallersPosition
{
    extension(UserGroupBalanceResponse position)
    {
        /// <summary>
        /// Both directions of what the caller owes and is owed, as payments with them on one
        /// end of every one.
        /// </summary>
        /// <remarks>
        /// Both, because both are theirs to state. A creditor recording "they paid me" is
        /// somebody saying money arrived, which is exactly what the Settle button has always
        /// let them say; the direction on it exists for that reason. What nobody may state is
        /// a payment they were not party to, and there are none of those here.
        /// </remarks>
        internal IReadOnlyList<SettlementPayment> Payments(Guid callerId) =>
        [
            .. from debt in position.YouOwed
               select new SettlementPayment
               {
                   FromUserId = callerId,
                   ToUserId = debt.UserId,
                   ToUserName = debt.UserName,
                   Amount = debt.Amount
               },
            .. from credit in position.OwedToYou
               select new SettlementPayment
               {
                   FromUserId = credit.UserId,
                   FromUserName = credit.UserName,
                   ToUserId = callerId,
                   Amount = credit.Amount
               }
        ];
    }
}

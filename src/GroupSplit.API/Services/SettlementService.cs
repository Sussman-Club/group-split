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
/// Every payment settling up writes has the caller on one end of it. That is the whole shape
/// of it: a member can say what they paid and what they were paid, because both are things
/// they were there for. A group-wide version would have one person recording money moving
/// between two others on their behalf, which is not what a button called Settle Up should
/// quietly do.
/// </para>
/// <para>
/// Recording money between two other members is possible, but only by asking for it by name
/// -- see <see cref="ISettlementService.RecordRepayment"/>. Keeping it a separate call is the
/// point: settling up stays personal, and stating something about two other people stays
/// something you had to mean.
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

    /// <summary>
    /// Records one repayment between two members, both named, whichever of them the caller
    /// is -- including neither.
    /// </summary>
    /// <remarks>
    /// The exception to everything said above, and the reason it is a separate call rather
    /// than a looser <see cref="SettleUp"/>: settling up stays personal, and a member who
    /// wants to state something about two other people has to say so explicitly.
    /// <para>
    /// What it is for is a ledger the group already agreed on -- months closed years ago in
    /// a spreadsheet, moving in. Balances are cumulative, so those repayments have to be
    /// written for the imbalance their expenses carry to ever clear, and the person moving
    /// them is on neither end of most of them.
    /// </para>
    /// </remarks>
    Task<SettlementPayment> RecordRepayment(Guid groupId, RecordRepaymentRequest request,
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

    public async Task<SettlementPayment> RecordRepayment(Guid groupId,
        RecordRepaymentRequest request, CancellationToken cancellationToken = default)
    {
        // Scoped to the caller's own groups, as everywhere else: a group they are not in is
        // not found, rather than one they can write repayments into.
        var group = await (await groups.GetGroupById(groupId, cancellationToken))
                        .Include(candidate => candidate.Users)
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        if (request.FromUserId == request.ToUserId)
            throw new ConflictException(ErrorCodes.SettlementWithSelf,
                "A settlement needs two different people.");

        var from = group.Users.FirstOrDefault(member => member.Id == request.FromUserId)
                   ?? throw new NotFoundException(ErrorCodes.UserNotFound,
                       "The member who paid is not in this group.");

        var to = group.Users.FirstOrDefault(member => member.Id == request.ToUserId)
                 ?? throw new NotFoundException(ErrorCodes.UserNotFound,
                     "The member who was paid is not in this group.");

        // Whatever the caller says, not what a balance implies. Nothing is checked against
        // what is outstanding: a group entering its history writes repayments for months
        // whose expenses are already in, and a repayment that overshoots today's balance is
        // an ordinary thing anyway -- somebody rounding up, or paying ahead.
        var date = (request.Date ?? DateTimeOffset.UtcNow).ToUniversalTime();

        var description = request.Description?.Trim() is { Length: > 0 } note ? note : null;

        context.Add(Transfer.Between(group, from, to, request.Amount, date, description));

        await context.SaveChangesAsync(cancellationToken);

        return new SettlementPayment
        {
            FromUserId = from.Id,
            FromUserName = $"{from.FirstName} {from.LastName}".Trim(),
            ToUserId = to.Id,
            ToUserName = $"{to.FirstName} {to.LastName}".Trim(),
            Amount = request.Amount
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

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

    /// <summary>
    /// How the caller clears everything, in the fewest payments, across every group at once.
    /// </summary>
    /// <remarks>
    /// The per-group minimisation the balances view already runs, run over every group and
    /// then added up by person. That last step is the whole point: a balance belongs to a
    /// group, but a payment belongs to a person, and somebody who owes the same friend in
    /// two groups pays them once.
    /// </remarks>
    Task<SettlementPlanResponse> GetPlan(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records one payment between the caller and one other person, spread over whichever
    /// groups the debt between them lives in, in a single save.
    /// </summary>
    /// <exception cref="ConflictException">
    /// There is nothing outstanding between the two of them in that direction.
    /// </exception>
    Task<SettleWithPersonResponse> SettleWithPerson(SettleWithPersonRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every repayment the caller was party to, in any group, newest first.
    /// </summary>
    /// <remarks>
    /// It answers "did I already pay this?", which is the question that stops people
    /// settling twice, and which until now could only be answered by opening each group's
    /// activity in turn.
    /// </remarks>
    Task<IQueryable<SettlementResponse>> GetHistory(CancellationToken cancellationToken = default);
}

public class SettlementService(
    ICurrentUser userContext,
    IGroupService groups,
    IDebtCalculationService debtCalculator,
    IGroupParticipants participants,
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

        // Before the two lookups below, which would report somebody the caller can see on
        // the balances page as not being in the group at all. An invitee holds a position
        // and has no account to pay or be paid; the position simply stands until they join.
        await RefuseIfPendingInvitee(group.Id, request.FromUserId, cancellationToken);
        await RefuseIfPendingInvitee(group.Id, request.ToUserId, cancellationToken);

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

    public async Task<SettlementPlanResponse> GetPlan(CancellationToken cancellationToken = default)
    {
        var (youPay, owedToYou) = await PlanSides(cancellationToken);

        var youOwe = youPay.Sum(person => person.Amount);
        var owed = owedToYou.Sum(person => person.Amount);

        var callerId = userContext.User.Id;

        // The newest repayment the caller was on either end of, in any group. Null when
        // they have never settled, which is a different thing from having settled long ago
        // and is why the screen can say "never" rather than showing nothing.
        var lastSettled = await context.Set<Transfer>()
            .Where(transfer => transfer.UserId == callerId ||
                               transfer.Splits.Any(split => split.UserId == callerId))
            .OrderByDescending(transfer => transfer.DateTime)
            .Select(transfer => (DateTimeOffset?)transfer.DateTime)
            .FirstOrDefaultAsync(cancellationToken);

        return new SettlementPlanResponse(owed - youOwe, owed, youOwe, youPay, owedToYou, lastSettled);
    }

    public async Task<SettleWithPersonResponse> SettleWithPerson(SettleWithPersonRequest request,
        CancellationToken cancellationToken = default)
    {
        var caller = userContext.User;

        if (request.UserId == caller.Id)
            throw new ConflictException(ErrorCodes.SettlementWithSelf,
                "A settlement needs two different people.");

        var (youPay, owedToYou) = await PlanSides(cancellationToken);

        // Which list the person has to be in follows from the direction stated on the
        // request, not from a balance read a second time: somebody recording "I paid them"
        // is acting precisely while the balance still says they owe.
        var side = request.Direction is SettlementDirection.YouPaidThem ? youPay : owedToYou;

        var person = side.FirstOrDefault(entry => entry.UserId == request.UserId)
                     ?? throw new ConflictException(ErrorCodes.SettlementNothingToSettle,
                         "There is nothing outstanding between the two of you in that direction.");

        var allocations = Allocate(request.Amount, person.Groups);

        var groups = await context.Set<Group>()
            .Include(@group => @group.Users)
            .Where(@group => allocations.Select(allocation => allocation.GroupId).Contains(@group.Id))
            .ToDictionaryAsync(@group => @group.Id, cancellationToken);

        var other = await context.Set<User>()
                        .FirstOrDefaultAsync(user => user.Id == request.UserId, cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.UserNotFound, "That person was not found.");

        var date = (request.Date ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var description = request.Description?.Trim() is { Length: > 0 } note ? note : null;

        var (from, to) = request.Direction is SettlementDirection.YouPaidThem
            ? (caller, other)
            : (other, caller);

        foreach (var allocation in allocations)
        {
            context.Add(Transfer.Between(groups[allocation.GroupId], from, to, allocation.Amount, date,
                description));
        }

        // One save for the whole payment. Two groups disagreeing about whether a single
        // transfer happened is the failure this screen exists to prevent, not to introduce.
        await context.SaveChangesAsync(cancellationToken);

        return new SettleWithPersonResponse(
            other.Id,
            $"{other.FirstName} {other.LastName}".Trim(),
            request.Amount,
            request.Direction,
            date,
            description,
            allocations);
    }

    public async Task<IQueryable<SettlementResponse>> GetHistory(
        CancellationToken cancellationToken = default)
    {
        var callerId = userContext.User.Id;
        var mine = await groups.GetAllGroups(cancellationToken);

        // Scoped to the caller's own groups, and then to the transfers they were party to.
        // A repayment between two other members belongs to the group's history; this is a
        // list of payments somebody made or received.
        return from transfer in context.Set<Transfer>()
               where mine.Any(@group => @group.Id == transfer.GroupId)
                     && (transfer.UserId == callerId ||
                         transfer.Splits.Any(split => split.UserId == callerId))
               from split in transfer.Splits
               select new SettlementResponse
               {
                   Id = transfer.Id,
                   GroupId = transfer.GroupId!.Value,
                   GroupName = transfer.Group!.Name,
                   FromUserId = transfer.UserId,
                   FromUserName = transfer.User.FirstName +
                                  (transfer.User.LastName != null ? " " + transfer.User.LastName : ""),
                   ToUserId = split.UserId,
                   ToUserName = split.User.FirstName +
                                (split.User.LastName != null ? " " + split.User.LastName : ""),
                   Amount = transfer.Amount,
                   DateTime = transfer.DateTime,
                   Description = transfer.Description,
                   PaidByYou = transfer.UserId == callerId
               };
    }

    /// <summary>
    /// Refuses a repayment naming somebody the group has invited and is waiting on.
    /// </summary>
    private async Task RefuseIfPendingInvitee(Guid groupId, Guid userId, CancellationToken ct)
    {
        if (await participants.IsPendingInvitee(groupId, userId, ct))
            throw new ConflictException(ErrorCodes.SettlementWithPendingInvitee,
                "That person has been invited to the group and has not joined yet, so there is " +
                "nobody to settle up with. Their balance stands until they accept.");
    }

    /// <summary>
    /// Both halves of the cross-group plan: what the caller owes by person, and what they
    /// are owed by person, each largest first.
    /// </summary>
    /// <remarks>
    /// Per group first and then added up, which is the only order that gives an answer
    /// anybody can act on. Minimising across the union of every group would produce lines
    /// like "pay Sofia, who pays Daniel" between people who are not in a group together and
    /// have no reason to be moving money to each other.
    /// </remarks>
    private async Task<(IReadOnlyList<PersonSettlement> YouPay, IReadOnlyList<PersonSettlement> OwedToYou)>
        PlanSides(CancellationToken cancellationToken)
    {
        var rows = await (await groups.GetAllGroupNetBalances(cancellationToken))
            .ToListAsync(cancellationToken);

        var youPay = new Dictionary<Guid, PersonBuilder>();
        var owedToYou = new Dictionary<Guid, PersonBuilder>();

        foreach (var inGroup in rows.GroupBy(row => new { row.GroupId, row.GroupName }))
        {
            var balances = inGroup
                .Select(row => new GroupNetBalance
                {
                    UserId = row.UserId,
                    UserName = row.UserName,
                    AmountPaid = row.AmountPaid,
                    AmountOwed = row.AmountOwed,
                    Balance = row.Balance,
                    // Carried through, so the minimisation can leave them out of the plan
                    // while their row goes on making the group's column add up.
                    IsPendingInvitee = row.IsPendingInvitee
                })
                .ToList();

            // Nothing outstanding here at all. Worth skipping rather than minimising an
            // all-zero group, which produces no payments anyway.
            if (balances.TrueForAll(balance => balance.Balance == 0))
                continue;

            var position = await debtCalculator.GetUserBalance(balances);

            foreach (var debt in position.YouOwed)
                Accumulate(youPay, debt, inGroup.Key.GroupId, inGroup.Key.GroupName);

            foreach (var credit in position.OwedToYou)
                Accumulate(owedToYou, credit, inGroup.Key.GroupId, inGroup.Key.GroupName);
        }

        return (Finish(youPay), Finish(owedToYou));
    }

    private static void Accumulate(Dictionary<Guid, PersonBuilder> side, DebtInfo debt,
        Guid groupId, string groupName)
    {
        if (debt.Amount <= 0)
            return;

        if (!side.TryGetValue(debt.UserId, out var person))
            side[debt.UserId] = person = new PersonBuilder(debt.UserName);

        person.Groups.Add(new GroupDebt(groupId, groupName, debt.Amount));
    }

    /// <summary>
    /// Largest person first, and within a person largest group first -- which is both how
    /// somebody reads the list and the order <see cref="Allocate"/> spends a payment in.
    /// </summary>
    private static IReadOnlyList<PersonSettlement> Finish(Dictionary<Guid, PersonBuilder> side) =>
    [
        .. from entry in side
           let amount = entry.Value.Groups.Sum(@group => @group.Amount)
           orderby amount descending, entry.Value.Name
           select new PersonSettlement(
               entry.Key,
               entry.Value.Name,
               amount,
               [.. entry.Value.Groups.OrderByDescending(@group => @group.Amount).ThenBy(@group => @group.GroupName)])
    ];

    /// <summary>
    /// Spends one payment over the groups the debt spans, largest group first.
    /// </summary>
    /// <remarks>
    /// The caller does not choose, and that is the point of the screen: which group a
    /// payment lands in is bookkeeping, and the person handing over the money should not
    /// have to do it. Largest first clears whole groups soonest, so a partial payment
    /// leaves the fewest groups half-settled.
    /// <para>
    /// Anything beyond what is outstanding -- somebody rounding up -- goes onto the largest
    /// group. It has to land somewhere, and that is where an overpayment is least
    /// surprising to find.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<GroupDebt> Allocate(decimal amount, IReadOnlyList<GroupDebt> groups)
    {
        var allocations = new List<GroupDebt>(groups.Count);
        var remaining = amount;

        foreach (var @group in groups)
        {
            if (remaining <= 0)
                break;

            var part = Math.Min(remaining, @group.Amount);
            allocations.Add(@group with { Amount = part });
            remaining -= part;
        }

        if (remaining > 0 && allocations.Count > 0)
            allocations[0] = allocations[0] with { Amount = allocations[0].Amount + remaining };

        return allocations;
    }

    private sealed class PersonBuilder(string name)
    {
        public string Name { get; } = name;

        public List<GroupDebt> Groups { get; } = [];
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

using System.Globalization;
using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Settling a group up in one action, over whatever part of it is being settled.
/// </summary>
/// <remarks>
/// Settling is sweeping. A run takes the transactions its scope matches out of the
/// outstanding pile, works out the fewest payments that bring their net to zero, writes
/// those payments, and marks the whole set -- payments included -- as swept by pointing it
/// at the run.
/// <para>
/// Including the payments is what keeps everything else honest. Their net is exactly the
/// negative of the net of the rest of the set, so sweeping the set leaves the all-time
/// balance equal to the balance of what is still outstanding. Which means
/// <c>GetGroupNetBalance</c>, the check that stops an unsettled member leaving and the one
/// that stops an unsettled account being deleted all go on being right, unchanged, no
/// matter how anybody scopes a run. There is no period-aware balance anywhere, because
/// there does not need to be one.
/// </para>
/// <para>
/// It follows that an expense back-dated into a month already settled is simply
/// outstanding, and the next run picks it up. Nothing reopens, nothing is corrected, and
/// nobody has to decide what a closed period means.
/// </para>
/// </remarks>
public interface ISettlementService
{
    /// <summary>
    /// What settling up would do, without recording any of it.
    /// </summary>
    Task<SettleUpPreviewResponse> Preview(Guid groupId, TransactionFilter? scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records it: the payments, and the sweep that says what they settled. All of it or
    /// none of it.
    /// </summary>
    Task<SettlementRunResponse> SettleUp(Guid groupId, SettleUpRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The settlings-up a group has had, newest first.
    /// </summary>
    Task<IReadOnlyList<SettlementRunResponse>> List(Guid groupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes one: everything it swept goes back to outstanding, and the group is where it
    /// was before anybody pressed the button.
    /// </summary>
    /// <remarks>
    /// What it does not do is take the money back. The payments it wrote stay exactly where
    /// they are, because that money really moved -- they simply become outstanding rows
    /// again, counted the way an earlier hand-recorded repayment is: as a credit. Somebody
    /// who has paid 50 towards a bill that turns out to be 120 is then asked for the
    /// difference, not for the whole of their new share.
    /// <para>
    /// This is what makes a settled expense editable again, and it is deliberately a
    /// decision somebody takes rather than something an edit does on their behalf. Reopening
    /// a month puts every payment in it back to outstanding, which is far too large a thing
    /// to happen quietly because a hotel bill was corrected.
    /// </para>
    /// </remarks>
    Task<SettlementRunResponse> Reopen(Guid groupId, Guid runId,
        CancellationToken cancellationToken = default);
}

public class SettlementService(
    ICurrentUser userContext,
    IGroupService groups,
    IDebtCalculationService debtCalculator,
    AppDbContext context) : ISettlementService
{
    public async Task<SettleUpPreviewResponse> Preview(Guid groupId, TransactionFilter? scope,
        CancellationToken cancellationToken = default)
    {
        var (_, swept) = await Sweepable(groupId, scope, cancellationToken);
        var balances = await NetOver(swept, cancellationToken);

        return new SettleUpPreviewResponse
        {
            Payments = debtCalculator.Minimize(balances),
            Balances = balances,
            TransactionCount = swept.Count,
            Total = swept.OfType<Expense>().Sum(expense => expense.Amount),
            CoversFrom = swept.Count is 0 ? null : swept.Min(transaction => transaction.DateTime),
            CoversTo = swept.Count is 0 ? null : swept.Max(transaction => transaction.DateTime),
            SuggestedLabel = SuggestLabel(swept)
        };
    }

    public async Task<SettlementRunResponse> SettleUp(Guid groupId, SettleUpRequest request,
        CancellationToken cancellationToken = default)
    {
        var (group, swept) = await Sweepable(groupId, request.Scope, cancellationToken);

        // Nothing to sweep is not an empty run, it is a request that missed. Writing one
        // anyway would let the button be pressed all afternoon and leave a row each time
        // saying nothing happened. A scope that matches transactions which are already
        // square is a different thing and does go through: it has a period to close, it
        // just has no money to move.
        if (swept.Count is 0)
            throw new ConflictException(ErrorCodes.SettlementNothingToSettle,
                "There is nothing outstanding to settle.");

        var balances = await NetOver(swept, cancellationToken);
        var payments = debtCalculator.Minimize(balances);

        // A run scoped to "up to 30 September" dates its payments 30 September unless told
        // otherwise, because that is what closing a month means and having to say it twice
        // is how somebody ends up with September's settlement sitting in October. Stated
        // beats derived, so an explicit date still wins.
        var effectiveDate = (request.EffectiveDate ?? request.Scope?.To ?? DateTimeOffset.UtcNow)
            .ToUniversalTime();

        var label = string.IsNullOrWhiteSpace(request.Label)
            ? SuggestLabel(swept)
            : request.Label.Trim();

        var run = new SettlementRun
        {
            Group = group,
            Label = label,
            RanBy = userContext.User,
            RanAt = DateTimeOffset.UtcNow,
            EffectiveDate = effectiveDate
        };

        context.Add(run);

        var members = await MembersIn(payments, cancellationToken);

        foreach (var payment in payments)
        {
            var transfer = Transfer.Between(group, members[payment.FromUserId], members[payment.ToUserId],
                payment.Amount, effectiveDate, label);

            // Part of what the run settles, and not an afterthought: this is the line that
            // makes the outstanding balance and the all-time balance the same number.
            transfer.SettlementRun = run;

            // And a payment of this run's, as against one it merely swept. The two are
            // different questions and a reopening answers them differently.
            transfer.WrittenByRun = run;

            context.Add(transfer);
        }

        foreach (var transaction in swept)
            transaction.SettlementRun = run;

        // One save, so a group is never left with the payments written and the sweep not,
        // which would be a settling-up that could be run again over what it had just paid.
        await context.SaveChangesAsync(cancellationToken);

        // Described from the run itself rather than from the pieces it was built out of, so
        // a run reads the same here as it does from the listing.
        return Describe(run);
    }

    public async Task<IReadOnlyList<SettlementRunResponse>> List(Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var groupQuery = await groups.GetGroupById(groupId, cancellationToken);

        if (!await groupQuery.AnyAsync(cancellationToken))
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        var runs = await context.Set<SettlementRun>()
            .Where(run => run.GroupId == groupId)
            .OrderByDescending(run => run.EffectiveDate)
            .Include(run => run.RanBy)
            .Include(run => run.Transactions)
            .ThenInclude(transaction => transaction.User)
            .Include(run => run.Transactions)
            .ThenInclude(transaction => transaction.Splits)
            .ThenInclude(split => split.User)
            .Include(run => run.Payments)
            .ThenInclude(payment => payment.User)
            .Include(run => run.Payments)
            .ThenInclude(payment => payment.Splits)
            .ThenInclude(split => split.User)
            .ToListAsync(cancellationToken);

        return [.. runs.Select(Describe)];
    }

    public async Task<SettlementRunResponse> Reopen(Guid groupId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        var groupQuery = await groups.GetGroupById(groupId, cancellationToken);

        if (!await groupQuery.AnyAsync(cancellationToken))
            throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        var run = await context.Set<SettlementRun>()
                      .Where(candidate => candidate.Id == runId && candidate.GroupId == groupId)
                      .Include(candidate => candidate.RanBy)
                      .Include(candidate => candidate.Transactions)
                      .ThenInclude(transaction => transaction.User)
                      .Include(candidate => candidate.Transactions)
                      .ThenInclude(transaction => transaction.Splits)
                      .ThenInclude(split => split.User)
                      .Include(candidate => candidate.Payments)
                      .ThenInclude(payment => payment.User)
                      .Include(candidate => candidate.Payments)
                      .ThenInclude(payment => payment.Splits)
                      .ThenInclude(split => split.User)
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new NotFoundException(ErrorCodes.SettlementRunNotFound,
                      "That settling-up was not found.");

        if (run.ReopenedAt is not null)
            throw new ConflictException(ErrorCodes.SettlementRunAlreadyReopened,
                $"\"{run.Label}\" has already been reopened.");

        // Described before the markers come off, because the description is of what the run
        // settled and that is exactly what is about to stop being recorded on the rows.
        var described = Describe(run);

        // All of it, payments included. Putting back only the expenses would leave the
        // transfers that settled them marked done, and the next settling-up would ask for
        // the whole share again instead of the difference.
        foreach (var transaction in run.Transactions.ToList())
        {
            transaction.SettlementRun = null;
            transaction.SettlementRunId = null;
        }

        run.ReopenedAt = DateTimeOffset.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        return described with { ReopenedAt = run.ReopenedAt };
    }

    /// <summary>
    /// A run as it reads from the outside: what it was called, who ran it, and the payments
    /// it wrote -- read back off the transfers rather than kept a second time.
    /// </summary>
    private static SettlementRunResponse Describe(SettlementRun run)
    {
        var settled = run.Transactions.ToList();

        var payments = run.Payments
            .Select(transfer => new SettlementPayment
            {
                FromUserId = transfer.UserId,
                FromUserName = FullName(transfer.User) ?? "",
                ToUserId = transfer.Splits.Select(split => split.UserId).FirstOrDefault(),
                ToUserName = FullName(transfer.Splits.Select(split => split.User).FirstOrDefault()) ?? "",
                Amount = transfer.Amount
            })
            .ToList();

        return new SettlementRunResponse
        {
            Id = run.Id,
            Label = run.Label,
            RanAt = run.RanAt,
            EffectiveDate = run.EffectiveDate,
            ReopenedAt = run.ReopenedAt,
            RanByUserId = run.RanByUserId,
            RanByUserName = FullName(run.RanBy),
            Payments = payments,
            TransactionCount = settled.Count,
            Total = settled.OfType<Expense>().Sum(expense => expense.Amount),
            CoversFrom = settled.Count is 0 ? null : settled.Min(transaction => transaction.DateTime),
            CoversTo = settled.Count is 0 ? null : settled.Max(transaction => transaction.DateTime)
        };
    }

    /// <summary>
    /// The group, and the outstanding transactions in it the scope matches.
    /// </summary>
    private async Task<(Group Group, List<Transaction> Swept)> Sweepable(Guid groupId, TransactionFilter? scope,
        CancellationToken cancellationToken)
    {
        // Scoped to the caller's own groups, so a group they are not in is not found rather
        // than empty -- an empty answer here would read as "already settled".
        var group = await (await groups.GetGroupById(groupId, cancellationToken))
                        .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

        // Hoisted: inside a query expression `group` is a keyword, not the variable.
        var id = group.Id;

        var outstanding =
            from transaction in context.Set<Transaction>()
            where transaction.GroupId == id && transaction.SettlementRunId == null
            select transaction;

        var swept = await outstanding.ApplyScope(scope).ToListAsync(cancellationToken);

        return (group, swept);
    }

    /// <summary>
    /// Where each person stands over one set of transactions: what they paid out of it, and
    /// what of it fell to them.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>GetGroupNetBalance</c>. That one answers over the whole group and
    /// over everybody currently in it; this one answers over a set, and over whoever appears
    /// in that set -- which is what a run has to settle, and includes somebody who has since
    /// left as long as one of these rows still names them.
    /// </remarks>
    private async Task<IReadOnlyList<GroupNetBalance>> NetOver(List<Transaction> swept,
        CancellationToken cancellationToken)
    {
        if (swept.Count is 0)
            return [];

        var ids = swept.Select(transaction => transaction.Id).ToList();

        var shares = await (
                from split in context.Set<TransactionSplit>()
                where ids.Contains(split.TransactionId)
                select new { split.UserId, split.Amount })
            .ToListAsync(cancellationToken);

        var paid = swept
            .GroupBy(transaction => transaction.UserId)
            .ToDictionary(byPayer => byPayer.Key, byPayer => byPayer.Sum(transaction => transaction.Amount));

        var owed = shares
            .GroupBy(share => share.UserId)
            .ToDictionary(byMember => byMember.Key, byMember => byMember.Sum(share => share.Amount));

        var names = await NamesOf([.. paid.Keys.Union(owed.Keys)], cancellationToken);

        return
        [
            ..from userId in names.Keys
            let amountPaid = paid.GetValueOrDefault(userId)
            let amountOwed = owed.GetValueOrDefault(userId)
            select new GroupNetBalance
            {
                UserId = userId,
                UserName = names[userId],
                AmountPaid = amountPaid,
                AmountOwed = amountOwed,
                Balance = amountPaid - amountOwed
            }
        ];
    }

    private async Task<Dictionary<Guid, string>> NamesOf(List<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var users = await (
                from user in context.Set<User>()
                where userIds.Contains(user.Id)
                select new { user.Id, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => user.Id, user => $"{user.FirstName} {user.LastName}");
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

    private static string? FullName(User? user) =>
        user is null ? null : $"{user.FirstName} {user.LastName}";

    /// <summary>
    /// A name for a run nobody has named, taken from the dates it actually covers rather
    /// than from the scope that selected them -- "settle everything outstanding" carries no
    /// dates at all and still lands on a definite stretch of time, and the stretch is what
    /// people recognise.
    /// </summary>
    private static string SuggestLabel(List<Transaction> swept)
    {
        if (swept.Count is 0)
            return "Settle-up";

        var from = swept.Min(transaction => transaction.DateTime);
        var to = swept.Max(transaction => transaction.DateTime);

        // Invariant, not the request's culture: this is a stored label that everybody in the
        // group reads, so it must not depend on whose browser wrote it.
        var start = from.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var end = to.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        return start == end ? start : $"{start} – {end}";
    }
}

file static class SettlementScope
{
    /// <summary>
    /// Narrows what a settling-up will sweep, by the same filter that narrows an expense
    /// listing.
    /// </summary>
    /// <remarks>
    /// Over transactions rather than expenses, because a run has to sweep the transfers in
    /// its reach too: an earlier hand-recorded payment is money that has already moved, and
    /// a run that ignored it would ask for it again.
    /// <para>
    /// <c>GroupId</c> and <c>Personal</c> are not read. The group is the one being settled,
    /// and personal expenses belong to no group and so to no settling-up. <c>Category</c>
    /// keeps only expenses, since a transfer is not filed under anything -- which is right:
    /// a run scoped to a category settles that category's spending, and marks its own
    /// payments swept itself rather than by matching them against the scope.
    /// </para>
    /// </remarks>
    extension(IQueryable<Transaction> transactions)
    {
        internal IQueryable<Transaction> ApplyScope(TransactionFilter? filter)
        {
            if (filter is null)
                return transactions;

            // Hoisted and normalised before the expression, for the reasons the expense
            // listing's filter gives: Npgsql wants UTC on a timestamptz parameter, and
            // lowering the strings once keeps the comparison off the database's collation.
            var after = filter.From?.ToUniversalTime();
            var before = filter.To?.ToUniversalTime();
            var category = filter.Category?.Trim().ToLowerInvariant();
            var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim().ToLowerInvariant();

            return from transaction in transactions
                   where (after == null || transaction.DateTime >= after) &&
                         (before == null || transaction.DateTime <= before) &&
                         (filter.PaidByUserId == null || transaction.UserId == filter.PaidByUserId) &&
                         (category == null ||
                          (transaction is Expense &&
                           ((Expense)transaction).Category != null &&
                           ((Expense)transaction).Category!.Name.ToLower() == category)) &&
                         (search == null ||
                          transaction.Name.ToLower().Contains(search) ||
                          (transaction.Description != null && transaction.Description.ToLower().Contains(search)) ||
                          (transaction.User.FirstName != null &&
                           transaction.User.FirstName.ToLower().Contains(search)) ||
                          (transaction.User.LastName != null &&
                           transaction.User.LastName.ToLower().Contains(search)))
                   select transaction;
        }
    }
}

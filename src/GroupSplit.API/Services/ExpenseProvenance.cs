using GroupSplit.API.Errors;
using GroupSplit.Data;
using GroupSplit.Data.Entities;
using GroupSplit.Shared;
using GroupSplit.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.API.Services;

/// <summary>
/// Which division produced an expense's shares -- and never what those shares are.
/// </summary>
/// <remarks>
/// Two operations, one rule between them: not a single stored amount moves. The splitter is
/// not called and is not reachable from here; the only column either of these writes is
/// <see cref="Transaction.SplitRuleVersionId"/>.
/// <para>
/// That is the whole reason this is not part of <see cref="ITransactionService"/>. Everything
/// there ends in a division, because an edit to an amount or a payer has to; these end in a
/// pointer. Keeping them apart means the guarantee is structural rather than a promise in a
/// comment: a change here that started moving money would have to reach for a dependency
/// this class does not take.
/// </para>
/// </remarks>
public interface IExpenseProvenance
{
    /// <summary>
    /// Points every expense in a group at the version of its category's rule that was in
    /// force on the day it was spent.
    /// </summary>
    /// <exception cref="NotFoundException">The caller is not in that group.</exception>
    Task<ReattachSummaryResponse> Reattach(
        ReattachTransactionsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Records what divided one expense, or -- with null -- that its shares are its own.
    /// </summary>
    /// <exception cref="NotFoundException">No expense with that id is the caller's to read.</exception>
    /// <exception cref="ConflictException">
    /// The caller has left the expense's group, or the version named belongs to another
    /// group's rule.
    /// </exception>
    Task SetDivisionSource(
        Guid transactionId, SetDivisionSourceRequest request, CancellationToken ct = default);
}

/// <inheritdoc cref="IExpenseProvenance"/>
public sealed class ExpenseProvenance(ICurrentUser userContext, AppDbContext dbContext)
    : IExpenseProvenance
{
    public async Task<ReattachSummaryResponse> Reattach(
        ReattachTransactionsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var groupId = (await CallersGroup(request.GroupId, ct)).Id;

        // Expenses only. A transfer is one payment to one person and was never divided by
        // anything, so it has no provenance to correct.
        var expenses = dbContext.Set<Expense>().Where(expense => expense.GroupId == groupId);

        // A dry run must leave nothing behind it. Reading without tracking is what makes that
        // true by construction rather than by remembering not to save: the rows below are
        // mutated either way, and on a dry run they are copies the context has never heard of.
        var rows = request.DryRun
            ? await expenses.AsNoTracking().ToListAsync(ct)
            : await expenses.ToListAsync(ct);

        var ruleOfCategory = await dbContext.Set<Category>()
            .Where(category => category.Group.Id == groupId && category.DefaultSplitRuleId != null)
            .ToDictionaryAsync(category => category.Id, category => category.DefaultSplitRuleId!.Value, ct);

        var names = await dbContext.Set<SplitRule>()
            .Where(rule => rule.Group.Id == groupId)
            .ToDictionaryAsync(rule => rule.Id, rule => rule.Name, ct);

        // Read as windows rather than as entities: nothing here needs a version's definition,
        // only when it opened and when it closed.
        var windows = (await dbContext.Set<SplitRuleVersion>()
                .Where(version => version.SplitRule.Group.Id == groupId)
                .Select(version => new Window(
                    version.Id, version.SplitRuleId, version.StartedAt, version.SupersededAt))
                .ToListAsync(ct))
            .GroupBy(window => window.SplitRuleId)
            .ToDictionary(rule => rule.Key, rule => rule.ToList());

        var tallies = new Dictionary<Guid, Tally>();
        var changed = 0;
        var uncovered = 0;

        foreach (var expense in rows)
        {
            var ruleId = expense.CategoryId is { } categoryId
                ? ruleOfCategory.GetValueOrDefault(categoryId)
                : null as Guid?;

            var version = ruleId is { } rule
                ? Covering(windows.GetValueOrDefault(rule), expense.DateTime)
                : null;

            if (version is null)
                uncovered++;

            if (ruleId is { } counted)
            {
                var tally = tallies.GetValueOrDefault(counted);
                tallies[counted] = tally with
                {
                    Examined = tally.Examined + 1,
                    Changed = tally.Changed + (expense.SplitRuleVersionId == version ? 0 : 1),
                    Uncovered = tally.Uncovered + (version is null ? 1 : 0)
                };
            }

            if (expense.SplitRuleVersionId == version)
                continue;

            changed++;

            // The pointer, and only the pointer. Splits are not read here, let alone written:
            // an expense's shares are the record of what each person owed and correcting the
            // account of what produced them cannot be allowed to restate them.
            expense.SplitRuleVersionId = version;
        }

        if (!request.DryRun)
            await dbContext.SaveChangesAsync(ct);

        return new ReattachSummaryResponse(
            groupId,
            request.DryRun,
            rows.Count,
            changed,
            uncovered,
            [.. tallies
                .Select(entry => new ReattachedRuleSummary(
                    entry.Key,
                    names.GetValueOrDefault(entry.Key, string.Empty),
                    entry.Value.Examined,
                    entry.Value.Changed,
                    entry.Value.Uncovered))
                .OrderBy(rule => rule.SplitRuleName, StringComparer.OrdinalIgnoreCase)]);
    }

    public async Task SetDivisionSource(
        Guid transactionId, SetDivisionSourceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentUser = userContext.User;
        var groups = dbContext.Entry(currentUser).Collection(user => user.Groups).Query();

        // Expenses only, scoped the way every other read of one is: something in a group the
        // caller is in, or something they paid for wherever it is.
        var expense = await dbContext.Set<Expense>()
            .FirstOrDefaultAsync(candidate =>
                candidate.Id == transactionId &&
                (groups.Any(@group => @group.Id == candidate.GroupId) ||
                 candidate.UserId == currentUser.Id), ct)
            ?? throw new NotFoundException(ErrorCodes.TransactionNotFound, "Transaction not found.");

        // Reading is wider than changing, here as everywhere: what divided an expense is part
        // of a group's account of its own money, and somebody who has left is no longer
        // writing it.
        if (expense.GroupId is { } groupId &&
            !await groups.AnyAsync(@group => @group.Id == groupId, ct))
        {
            throw new ConflictException(ErrorCodes.TransactionGroupLeft,
                "You are no longer in this transaction's group, so it cannot be changed.");
        }

        if (request.SplitRuleVersionId is { } versionId)
        {
            var belongs = await dbContext.Set<SplitRuleVersion>()
                .AnyAsync(version =>
                    version.Id == versionId &&
                    version.SplitRule.Group.Id == expense.GroupId, ct);

            // The same refusal whether the version is another group's or is nothing at all,
            // so guessing ids teaches nobody what rules another group keeps.
            if (!belongs)
                throw new ConflictException(ErrorCodes.SplitRuleVersionNotInGroup,
                        "That division is not one of this expense's group's, so it cannot be " +
                        "what divided it.")
                    .WithExtension("splitRuleVersionId", versionId);
        }

        expense.SplitRuleVersionId = request.SplitRuleVersionId;

        await dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The version of a rule that was in force on <paramref name="on"/>: the one whose window
    /// contains it. Null when none does, which is every date before the rule's history starts.
    /// </summary>
    /// <remarks>
    /// Half-open windows, closed at the start: a version that opened at midnight on the first
    /// covers the first, and the version it superseded does not. Two versions cannot both
    /// answer, because a chain's windows meet rather than overlap.
    /// </remarks>
    private static Guid? Covering(List<Window>? windows, DateTimeOffset on) =>
        windows?.FirstOrDefault(window =>
            window.StartedAt <= on && (window.SupersededAt is null || window.SupersededAt > on))?.Id;

    private async Task<Group> CallersGroup(Guid groupId, CancellationToken ct) =>
        await dbContext.Entry(userContext.User).Collection(user => user.Groups).Query()
            .FirstOrDefaultAsync(@group => @group.Id == groupId, ct)
        ?? throw new NotFoundException(ErrorCodes.GroupNotFound, "Group was not found.");

    /// <summary>When one version of a rule was what that rule said.</summary>
    private sealed record Window(
        Guid Id, Guid SplitRuleId, DateTimeOffset StartedAt, DateTimeOffset? SupersededAt);

    /// <summary>Running counts for one rule, while the pass walks the group's expenses.</summary>
    private readonly record struct Tally(int Examined, int Changed, int Uncovered);
}

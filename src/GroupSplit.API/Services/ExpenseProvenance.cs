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
/// Not a single stored amount moves. The splitter is not called and is not reachable from
/// here; the only column this writes is <see cref="Transaction.SplitRuleVersionId"/>.
/// <para>
/// That is the whole reason this is not part of <see cref="ITransactionService"/>. Everything
/// there ends in a division, because an edit to an amount or a payer has to; this ends in a
/// pointer. Keeping them apart means the guarantee is structural rather than a promise in a
/// comment: a change here that started moving money would have to reach for a dependency
/// this class does not take.
/// </para>
/// <para>
/// There was a second operation beside it -- a pass over a whole group, pointing every
/// expense at the version of its category's rule whose window covered the day it was spent.
/// It is gone. It resolved each expense through the category's rule <em>as the category
/// points now</em>, so a group that re-pointed a category wrote versions of a rule its old
/// expenses had never been divided by, and the pass had no way to know the difference: the
/// database records which version divided an expense, and has never recorded which rule a
/// category named in 2023.
/// </para>
/// </remarks>
public interface IExpenseProvenance
{
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
}

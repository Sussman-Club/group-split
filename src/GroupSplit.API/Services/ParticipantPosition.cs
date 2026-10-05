using GroupSplit.API.Errors;
using GroupSplit.Data.Entities;
using GroupSplit.Data.Extensions;
using GroupSplit.Shared.Errors;

namespace GroupSplit.API.Services;

/// <summary>
/// What one person holds on one transaction in one group: what they paid, and what they
/// owed.
/// </summary>
/// <remarks>
/// The unit the departed-position rule is stated in. Somebody can only leave a group with a
/// zero balance, and the group's balances list only the people still in it, so the balances
/// sum to zero for exactly as long as nothing changes what a departed person paid or owed.
/// A position is that "what they paid or owed", per transaction, so it can be compared
/// before and after an edit.
/// </remarks>
public sealed record ParticipantPosition(Guid UserId, decimal Paid, decimal Owed)
{
    public bool IsNothing => Paid == 0m && Owed == 0m;

    /// <summary>
    /// Their position on the transaction as it stands, read in the group it was recorded in.
    /// Zero once the transaction has left that group: a move takes the position out of the
    /// group's column just as surely as a delete does.
    /// </summary>
    public static ParticipantPosition Of(Transaction transaction, Guid userId, Guid groupId) =>
        transaction.GroupId != groupId
            ? new ParticipantPosition(userId, 0m, 0m)
            : new ParticipantPosition(userId,
                transaction.Payer == userId ? transaction.Amount : 0m,
                transaction.Splits.Where(split => split.UserId == userId).Sum(split => split.Amount));
}

/// <summary>
/// The refusal for a change that would move a departed participant's position.
/// </summary>
public static class DepartedPositions
{
    /// <summary>
    /// Refuses when anybody in <paramref name="before"/> now holds something different on
    /// <paramref name="after"/>, read in the group the transaction was recorded in.
    /// </summary>
    public static void RefuseIfMoved(IReadOnlyCollection<ParticipantPosition> before, Transaction after,
        Guid groupId)
    {
        var moved = before
            .Where(position => ParticipantPosition.Of(after, position.UserId, groupId) != position)
            .Select(position => position.UserId)
            .ToList();

        if (moved.Count > 0)
            throw Refusal(moved);
    }

    /// <summary>
    /// The owed half only, against shares worked out and not written: a bill being divided,
    /// where the payer and the amount are not what is in question.
    /// </summary>
    public static void RefuseIfSharesMove(IReadOnlyCollection<ParticipantPosition> before,
        IEnumerable<SplitAmount> wouldBe)
    {
        var shares = wouldBe.ToList();

        var moved = before
            .Where(position => position.Owed != shares
                .Where(share => share.UserId == position.UserId)
                .Sum(share => share.Amount))
            .Select(position => position.UserId)
            .ToList();

        if (moved.Count > 0)
            throw Refusal(moved);
    }

    public static DomainException Refusal(IReadOnlyList<Guid> userIds) =>
        new ConflictException(ErrorCodes.TransactionMovesDepartedBalance,
                "This would change what somebody who has left the group paid or owed here. They " +
                "settled up before leaving, so their part of this transaction stays as it was " +
                "recorded. Its name, note, date and category can still change, and so can the " +
                "other shares, as long as theirs stays the same.")
            .WithExtension("userIds", userIds);
}

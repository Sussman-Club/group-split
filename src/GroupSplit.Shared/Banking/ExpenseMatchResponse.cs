namespace GroupSplit.Shared;

/// <summary>
/// An expense that could already be the same money as an imported row.
/// </summary>
/// <remarks>
/// A suggestion and nothing more. Nothing is merged, hidden, filed or ignored on the
/// strength of one: what it is for is naming the expense that is already there, clearly
/// enough that a person can say whether it is the same payment or a second one.
/// </remarks>
/// <param name="AmountDifference">
/// How far the two amounts are apart, never negative. A card can settle for more than the
/// receipt -- a tip added afterwards -- so this is worth showing rather than hiding.
/// </param>
/// <param name="DaysApart">
/// How many days lie between them. A card charge posts after the meal, so this is usually
/// not zero.
/// </param>
public sealed record ExpenseMatchResponse(
    Guid TransactionId,
    string Name,
    decimal Amount,
    string Currency,
    DateTimeOffset DateTime,
    Guid? GroupId,
    string? GroupName,
    string PaidByUserName,
    decimal AmountDifference,
    int DaysApart)
{
    /// <summary>Where it sits, for a sentence naming it: the group, or the person's own expenses.</summary>
    public string Where => GroupName is { Length: > 0 } group ? group : "your personal expenses";
}

/// <summary>
/// Attaches an imported row to an expense that is already recorded, instead of filing it as
/// a second one.
/// </summary>
public sealed record LinkBankTransactionRequest
{
    /// <summary>The expense the row is the same money as.</summary>
    public Guid TransactionId { get; set; }
}

/// <summary>
/// Says that an imported row and an expense are not the same money after all, so the pair
/// is never suggested again.
/// </summary>
public sealed record DismissBankMatchRequest
{
    /// <summary>The expense that was suggested and is not it.</summary>
    public Guid TransactionId { get; set; }
}

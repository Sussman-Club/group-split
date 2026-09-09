using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// How strong a claim a suggested match is.
/// </summary>
/// <remarks>
/// Two grades and not a score, because there are exactly two things done with one: a
/// <see cref="Confident"/> match is worth saying so on a row nobody has touched, and a
/// <see cref="Possible"/> one is only worth answering at the moment somebody files. A
/// number would invite a threshold, and a threshold is the thing that was chosen by
/// reasoning rather than by counting last time.
/// <para>
/// Ordered, so best-first sorting is the enum's own order. It travels as its name, like
/// <see cref="InboxStatus"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<MatchConfidence>))]
public enum MatchConfidence
{
    /// <summary>
    /// The amounts differ, but by something a duplicate could explain -- a tip, or a figure
    /// somebody rounded when they typed it.
    /// </summary>
    Possible,

    /// <summary>
    /// The same money to the cent, within the window. Measured against real spending, two
    /// unrelated expenses by the same person agree this closely 0.18% of the time.
    /// </summary>
    Confident
}

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
/// <param name="Confidence">
/// How strong the claim is. Worth reading before deciding how loudly to say it: only a
/// confident match is worth putting in front of somebody who has not touched the row.
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
    int DaysApart,
    MatchConfidence Confidence)
{
    /// <summary>The same money to the cent, and so worth interrupting somebody with.</summary>
    public bool IsConfident => Confidence == MatchConfidence.Confident;

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

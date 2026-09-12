using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared;

/// <summary>
/// Files one imported charge as several expenses, by saying which lines of its bill belong
/// to which purchase.
/// </summary>
/// <remarks>
/// A warehouse run is the flat's groceries and a jacket that is nobody's business but yours:
/// one card charge, two purchases. Filing it as a single expense makes the by-category
/// totals wrong and puts your clothes in the flat's ledger; filing it as two needs a way to
/// say which lines are which, and this is it.
/// <para>
/// Everything at once, and deliberately. Every line has to land in a part, so there is no
/// half-split state for anything to be left in and nothing to reconcile afterwards -- and
/// <c>Filed</c> stays the binary the inbox already treats it as, rather than growing a third
/// value three call sites would have to learn.
/// </para>
/// <para>
/// The parts' amounts are not sent. They are cut from the charge here, in proportion to the
/// lines each part holds, with the tax and the tip apportioned over them -- so the parts sum
/// to what the card was charged by construction rather than by the caller getting the
/// arithmetic right.
/// </para>
/// </remarks>
public record SplitBankTransactionRequest
{
    /// <summary>
    /// The purchases this charge turns out to be. At least two -- one part is an ordinary
    /// filing, which <c>POST /inbox/{id}/file</c> already does.
    /// </summary>
    [MinLength(2, ErrorMessage = "Splitting a charge needs at least two parts. To file it as one, file it.")]
    public IReadOnlyList<BankTransactionPartInput> Parts { get; init; } = [];

    /// <summary>
    /// Whether to go ahead even though the charge looks like an expense already recorded.
    /// Same meaning as on an ordinary filing.
    /// </summary>
    public bool FileAnyway { get; init; }
}

/// <summary>One purchase on a split charge, and the lines of the bill that make it up.</summary>
public record BankTransactionPartInput
{
    /// <summary>
    /// The group this part belongs to, or null to keep it on your own ledger. The jacket is
    /// the reason this is per part rather than per charge.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>What this part was for. A category belongs to a group, so a personal part has none.</summary>
    public Guid? CategoryId { get; init; }

    public Guid? PaidByUserId { get; init; }

    /// <summary>
    /// What to call this expense. Without one it takes the charge's own name, which is the
    /// merchant -- fine for the part that is most of the bill and poor for the rest, so a
    /// split worth making is usually worth naming.
    /// </summary>
    [StringLength(124, ErrorMessage = "Name must be less than 124 characters.")]
    public string? Name { get; init; }

    [StringLength(256, ErrorMessage = "Description must be less than 256 characters.")]
    public string? Description { get; init; }

    /// <summary>
    /// The lines of the bill that are this part's money. Every line of the bill has to appear
    /// in exactly one part.
    /// </summary>
    [MinLength(1, ErrorMessage = "A part needs at least one line of the bill.")]
    public IReadOnlyList<Guid> ItemIds { get; init; } = [];

    /// <summary>
    /// Exactly how to divide this part, or null to divide it the way its category says.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case: a part whose category names an itemised rule divides itself
    /// by the very lines being assigned here, which is the whole point of splitting before
    /// filing rather than after.
    /// </remarks>
    public IReadOnlyList<SplitInput>? Splits { get; init; }
}

/// <summary>What a split charge became.</summary>
/// <param name="Parts">
/// One entry per purchase, in the order they were sent, with the amount each was cut to.
/// </param>
public sealed record SplitBankTransactionResponse(
    Guid BankTransactionId,
    decimal Charge,
    IReadOnlyList<SplitPartResponse> Parts);

/// <summary>One purchase a split charge became.</summary>
public sealed record SplitPartResponse(
    Guid TransactionId,
    string Name,
    Guid? GroupId,
    decimal Amount,
    int ItemCount);

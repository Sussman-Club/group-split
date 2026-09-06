using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// What one person owes on one expense, as the client states it.
/// </summary>
/// <remarks>
/// The point of the whole reshape, arriving on the wire: a split is a fact about this
/// expense, decided when it is written, rather than something re-derived from a rule
/// afterwards. Sending these says "divide it exactly this way"; sending none says "divide
/// it the way the category says", which is what nearly every expense wants and what the
/// dialog offers first.
/// <para>
/// The amounts must sum to the expense's amount. Nothing else re-derives the division, so
/// a set that does not sum makes every balance in the group wrong with nothing to catch
/// it -- which is why the API refuses it rather than adjusting it.
/// </para>
/// </remarks>
public record SplitInput
{
    public Guid UserId { get; init; }

    /// <summary>
    /// May be negative, so that "Omar is owed ten back on this one" has a way to be said.
    /// It is the sum that has to come out right, not each part.
    /// </summary>
    [MaxDecimalPlaces(2, ErrorMessage = "A share must be a number with no more than 2 decimal places.")]
    public decimal Amount { get; init; }
}

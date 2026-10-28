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
    /// Goes the same way as the amount: zero or more on an expense, zero or less on a
    /// refund. Somebody owed money back is a settlement, not a negative share of a cost --
    /// a share the other way sums correctly and moves money nobody recorded as a payment.
    /// </summary>
    [MaxDecimalPlaces(2, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.SharePrecision))]
    public decimal Amount { get; init; }
}

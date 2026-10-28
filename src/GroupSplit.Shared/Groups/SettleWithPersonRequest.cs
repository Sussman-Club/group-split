using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// Record one payment between the caller and one other person, wherever the debt between
/// them lives -- one action for a payment that spans two groups.
/// </summary>
/// <remarks>
/// The cross-group counterpart of <see cref="SettleRequest"/>. That one settles inside a
/// group, because a balance belongs to a group; this one takes the person and the amount,
/// and writes the per-group transfers behind it. The person sees one payment and the ledger
/// stays honest.
/// <para>
/// Everything it writes is written in one save. A payment that half-recorded would leave
/// two groups disagreeing about whether it happened, with nothing on either side to say
/// which.
/// </para>
/// <para>
/// <b>How the amount is spread.</b> Largest group first, until it runs out. The caller does
/// not choose, and deliberately so: the whole point of the screen is that the split across
/// groups is bookkeeping the payer should not have to think about. Anything left over after
/// every outstanding group is cleared -- somebody rounding up -- goes onto the largest group
/// too, because a payment has to land somewhere and the largest is where an overpayment is
/// least surprising.
/// </para>
/// </remarks>
public record SettleWithPersonRequest
{
    /// <summary>The other end of the payment.</summary>
    public Guid UserId { get; set; }

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountRequired))]
    [MaxDecimalPlaces(2, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountPrecision))]
    [GreaterThan(0, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountPositive))]
    public decimal Amount { get; set; }

    /// <summary>
    /// Who paid whom. Stated rather than read off the balance, for the same reason
    /// <see cref="SettleRequest.Direction"/> is: a debtor recording "I paid you back" acts
    /// precisely while the balance still says they owe.
    /// </summary>
    public SettlementDirection Direction { get; set; } = SettlementDirection.TheyPaidYou;

    /// <summary>When the money moved. Null means now.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>What to remember about it -- "cash", "bank transfer, ref 4821".</summary>
    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionLength))]
    public string? Description { get; set; }

    /// <summary>
    /// The last day of activity it settles, or null for everything. See <see cref="Cutoff"/>.
    /// </summary>
    /// <remarks>
    /// Decides what is outstanding with them, and so which groups the payment is spread over
    /// and how much each one takes; every transfer it writes remembers it.
    /// </remarks>
    public DateOnly? Through { get; set; }

    /// <summary>
    /// The caller's offset from UTC in minutes, which says when <see cref="Through"/> ends.
    /// </summary>
    [Range(-Cutoff.MaxOffsetMinutes, Cutoff.MaxOffsetMinutes,
        ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.UtcOffset))]
    public int? UtcOffset { get; set; }
}

/// <summary>
/// What one cross-group payment recorded, and where it landed.
/// </summary>
/// <param name="Groups">
/// One entry per group a transfer was written in, with that group's part of the amount.
/// Sums to <paramref name="Amount"/>.
/// </param>
/// <param name="CoversThrough">The day it was settled through, when it was given one.</param>
public record SettleWithPersonResponse(
    Guid UserId,
    string UserName,
    decimal Amount,
    SettlementDirection Direction,
    DateTimeOffset Date,
    string? Description,
    IReadOnlyList<GroupDebt> Groups,
    DateOnly? CoversThrough = null);

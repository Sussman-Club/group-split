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

    [Required(ErrorMessage = "Amount is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Amount must be a number with no more than 2 decimal places.")]
    [GreaterThan(0, ErrorMessage = "Amount must be greater than 0.")]
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
    [StringLength(256, ErrorMessage = "Description must be 256 characters or fewer.")]
    public string? Description { get; set; }
}

/// <summary>
/// What one cross-group payment recorded, and where it landed.
/// </summary>
/// <param name="Groups">
/// One entry per group a transfer was written in, with that group's part of the amount.
/// Sums to <paramref name="Amount"/>.
/// </param>
public record SettleWithPersonResponse(
    Guid UserId,
    string UserName,
    decimal Amount,
    SettlementDirection Direction,
    DateTimeOffset Date,
    string? Description,
    IReadOnlyList<GroupDebt> Groups);

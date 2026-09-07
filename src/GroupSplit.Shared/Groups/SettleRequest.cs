using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>Which way the money went.</summary>
/// <remarks>
/// Stated rather than inferred from the balance. The two are the same thing nearly always,
/// and the exception is the case that matters: a debtor recording "I paid you back" is
/// acting precisely when the balance still says they owe, so reading the direction off the
/// balance would turn their repayment into a second debt.
/// </remarks>
public enum SettlementDirection
{
    /// <summary>The other member paid the caller. The creditor recording a repayment.</summary>
    TheyPaidYou = 0,

    /// <summary>The caller paid the other member. The debtor recording their own.</summary>
    YouPaidThem = 1
}

public record SettleRequest
{
    public Guid UserId { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Amount must be a number with no more than 2 decimal places.")]
    [GreaterThan(0, ErrorMessage = "Amount must be greater than 0.")]
    public decimal Amount { get; set; }

    /// <summary>
    /// Who paid whom. Defaults to the creditor's side, which is the only side the app could
    /// record before this and therefore what an unstated direction has always meant.
    /// </summary>
    public SettlementDirection Direction { get; set; } = SettlementDirection.TheyPaidYou;

    /// <summary>
    /// When the money actually moved. Null means now, which is what recording one has
    /// always meant and so is what an unstated date goes on meaning.
    /// </summary>
    /// <remarks>
    /// Stating it matters at the end of a month: a payment made on the 30th and typed in on
    /// the 3rd belongs to the month it settled, not the one somebody got round to it in.
    /// Until this existed the recorded moment was the only moment available, so a group
    /// closing September could not put September's payments in September.
    /// </remarks>
    public DateTimeOffset? Date { get; set; }

    /// <summary>
    /// What the payer wants remembered -- "cash", "bank transfer, ref 4821".
    /// </summary>
    /// <remarks>
    /// Optional, and deliberately so. A settling-up has to stay one tap, and a note nobody
    /// can skip is the thing that would stop it being one.
    /// </remarks>
    [StringLength(256, ErrorMessage = "Description must be 256 characters or fewer.")]
    public string? Description { get; set; }
}

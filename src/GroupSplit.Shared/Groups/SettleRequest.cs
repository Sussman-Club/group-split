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
}

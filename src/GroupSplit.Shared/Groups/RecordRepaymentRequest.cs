using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// Record a repayment between two members of a group, both named, whoever is asking.
/// </summary>
/// <remarks>
/// Settling and settling up both put the caller on one end, because what you paid and what
/// you were paid are things you were there for. This is the deliberate exception, and the
/// only one: a member stating that two other people squared up between themselves.
/// <para>
/// It exists for a ledger a group already agreed on. Years of months closed in a spreadsheet
/// and moved into a group are repayments that happened, not claims anybody is making now, and
/// the person doing the moving is on neither end of most of them. Without this the group can
/// load its expenses and nothing it is allowed to write will ever clear what they leave
/// outstanding.
/// </para>
/// <para>
/// Both ends are stated rather than inferred, and there is no direction to read off a balance
/// the way <see cref="SettlementDirection"/> has to: the caller says who handed money to
/// whom.
/// </para>
/// <para>
/// A transfer does not record who entered it, so one written this way is indistinguishable
/// from one the payer wrote themselves. That is a real gap and deliberately not closed here:
/// it wants a column and a backfill of its own rather than being smuggled in beside the
/// capability that makes it worth having.
/// </para>
/// </remarks>
public record RecordRepaymentRequest
{
    /// <summary>Who handed the money over.</summary>
    public Guid FromUserId { get; set; }

    /// <summary>Who received it.</summary>
    public Guid ToUserId { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Amount must be a number with no more than 2 decimal places.")]
    [GreaterThan(0, ErrorMessage = "Amount must be greater than 0.")]
    public decimal Amount { get; set; }

    /// <summary>
    /// When the money moved. Null means now.
    /// </summary>
    /// <remarks>
    /// It matters more here than anywhere else this field appears. A group transcribing its
    /// history is recording repayments that closed months years ago, and dating them the day
    /// somebody typed them in would put every one of them in the same week.
    /// </remarks>
    public DateTimeOffset? Date { get; set; }

    /// <summary>
    /// What to remember about it -- "cash", "end of September 2024".
    /// </summary>
    [StringLength(256, ErrorMessage = "Description must be 256 characters or fewer.")]
    public string? Description { get; set; }
}

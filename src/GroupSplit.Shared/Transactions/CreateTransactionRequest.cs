using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record CreateTransactionRequest
{
    public Guid? PaidByUserId { get; set; }

    /// <summary>
    /// The group the transaction is being recorded in, when the caller picked one.
    /// </summary>
    /// <remarks>
    /// The rule version already implies a group, so this is only consulted when none was
    /// given: it is what lets the API tell "this is a personal expense" apart from "I
    /// chose a group but there was no rule to select", which would otherwise both arrive
    /// as an absent <see cref="RuleVersionId"/> and be filed personally.
    /// </remarks>
    public Guid? GroupId { get; set; }

    public Guid? RuleVersionId { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(124, ErrorMessage = "Name must be less than 124 characters.")]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessage = "Description must be less than 256 characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Amount is required.")]
    [MaxDecimalPlaces(2, ErrorMessage = "Amount must be a number with no more than 2 decimal places.")]
    public decimal Amount { get; set; }

    [Required(ErrorMessage = "Date is required.")]
    public DateTimeOffset DateTime { get; set; }
};
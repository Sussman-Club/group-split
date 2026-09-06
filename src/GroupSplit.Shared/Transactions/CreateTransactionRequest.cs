using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record CreateTransactionRequest
{
    public Guid? PaidByUserId { get; set; }

    /// <summary>
    /// The group the expense is recorded in, or null for the caller's own.
    /// </summary>
    /// <remarks>
    /// It says outright which group it is now. It used to be inferred from the rule
    /// version, which meant "a personal expense" and "a group I picked that had no rule to
    /// select" both arrived as an absent rule and were filed personally -- so this field
    /// existed to tell those apart. The second case no longer exists: every group can
    /// record an expense, with or without a category.
    /// </remarks>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// What it is for, or null for none. A category may name a rule to divide by; without
    /// one, or without a category at all, the expense divides evenly.
    /// </summary>
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// Exactly how to divide it, or null to divide it the way the category says.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case and the dialog's default: the category's rule pre-fills
    /// the division, or an even split does when there is no category or it names no rule.
    /// Sending them says the person overrode that for this one expense -- "don't charge
    /// Omar for his own birthday cake" -- and they are stored as given.
    /// </remarks>
    public IReadOnlyList<SplitInput>? Splits { get; set; }

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
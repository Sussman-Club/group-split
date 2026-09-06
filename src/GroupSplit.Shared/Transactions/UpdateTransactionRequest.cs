using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record UpdateTransactionRequest
{
    [Required] public Guid PaidByUserId { get; set; }

    /// <summary>
    /// The group the expense belongs to after the edit, or null for the caller's own ledger.
    /// Same meaning as on create, and the way an expense moves: a personal one is shared by
    /// giving it a group, a shared one is taken back by giving it none.
    /// </summary>
    /// <remarks>
    /// This is what the bank-sync review inbox will do with every imported row it files --
    /// an import lands as the account holder's own and "share this with the flat" is a
    /// move -- so it exists as an ordinary edit rather than as an endpoint of its own. A
    /// move re-derives the division among the destination's members (the old shares name
    /// people who may not be in it), takes the destination's currency, and refuses a
    /// category from anywhere else. Only your own expense can be made personal: taking
    /// somebody else's out of a group would delete a debt owed to them.
    /// <para>
    /// The model a PATCH is applied to carries the current group, so a patch that says
    /// nothing about it leaves the expense where it is.
    /// </para>
    /// </remarks>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// What it is filed under, or null for none. Optional, unlike the rule version it
    /// replaces: an expense under no category divides evenly.
    /// </summary>
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// Exactly how to divide it, or null to divide it the way the category says.
    /// </summary>
    /// <remarks>
    /// This is the one field a JSON Patch has to be read for rather than merely applied.
    /// The model handed to a patch carries the splits the expense already has, so an
    /// operation may address one of them -- but a patch that says nothing about them means
    /// "recompute", not "keep these", because an edit to the amount, the payer or the
    /// category changes what everybody owed. The endpoint tells those apart by looking at
    /// the operations before applying them, and clears this when none touched it.
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
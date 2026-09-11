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
    /// Where it was spent, or null for none. See
    /// <see cref="CreateTransactionRequest.MerchantId"/>.
    /// </summary>
    /// <remarks>
    /// Editable, including on an expense that came from a bank: the sync sets it once and
    /// never follows the link again, so a provider that named the wrong shop is a thing a
    /// person can correct without the next sync undoing them.
    /// </remarks>
    public Guid? MerchantId { get; set; }

    /// <summary>
    /// Exactly how to divide it, or null to divide it the way the category says.
    /// </summary>
    /// <remarks>
    /// This is the one field a JSON Patch has to be read for rather than merely applied.
    /// The model handed to a patch carries the splits the expense already has, so an
    /// operation may address one of them.
    /// <para>
    /// What a patch that says nothing about them means is <em>never restate a division
    /// somebody made</em>. The endpoint clears this only when the patch moves the expense
    /// between groups, where the shares name people who may not be in the destination;
    /// every other edit carries them forward, and shares a person typed are then stored back
    /// exactly as they were -- so changing the amount alone on one of those is refused,
    /// because shares that summed to the old total do not sum to the new one.
    /// </para>
    /// <para>
    /// That is the opposite of what this comment used to claim, and the reversal was
    /// deliberate: silence meant "divide it again" until 2026-09-09, and the day before
    /// that a merchant-linking pass over 733 expenses re-divided every one of them from
    /// their category's rule and moved 1,394.72 onto one member. Keeping what somebody
    /// decided is the fix for that, and a metadata edit still cannot move money.
    /// </para>
    /// <para>
    /// A division <em>nobody</em> decided is treated as what it is: the output of a rule --
    /// or of an even split under no rule, which the app worked out just as surely -- over the
    /// expense's amount, payer, category and group. When an edit moves one of those four, the
    /// division is worked out again from the new values, by the version the expense was
    /// written under. It takes both halves: shares that reproduce the expense's own division,
    /// <em>and</em> an input having moved. Neither an absent operation on its own nor an
    /// input moving on its own is enough, which is what keeps the 2026-09-08 shape
    /// unreachable. See <c>TransactionSplitPatchTest</c> for the behaviour as it stands.
    /// </para>
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
using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

public record CreateTransactionRequest
{
    /// <summary>The reviewed itemized receipt to validate and save with the new expense.</summary>
    public SaveReceiptRequest? Receipt { get; set; }
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
    /// Where it was spent, or null for the ordinary case of nowhere anybody wrote down.
    /// </summary>
    /// <remarks>
    /// Filing an imported row sets this from the row itself, so it is here for the other
    /// way in: cash at the same shop every week, or a group with no bank linked. What it
    /// buys is the merchant's mark on the row and the shop being searchable -- it says
    /// nothing about how the expense divides, which is the category's job.
    /// </remarks>
    public Guid? MerchantId { get; set; }

    /// <summary>
    /// A rule to divide by, whatever the category says, or null to let the category decide.
    /// </summary>
    /// <remarks>
    /// For the expense that divides unlike its neighbours: the weekly shop is even, but this
    /// one is the birthday cake and all of it is Ana's. Naming the rule rather than typing
    /// the shares is what keeps it re-dividable -- correcting the amount afterwards divides
    /// again by the same rule, where shares somebody typed are refused because they no
    /// longer sum to the new total.
    /// <para>
    /// It has to be a rule of the expense's own group. Both this and
    /// <see cref="Splits"/> is refused: they are two answers to one question.
    /// </para>
    /// </remarks>
    public Guid? SplitRuleId { get; set; }

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

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameRequired))]
    [StringLength(TransactionNames.MaxLength, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameLength))]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionShort))]
    public string? Description { get; set; }

    /// <summary>
    /// What was spent, or what came back: negative records a refund, which the splitter
    /// divides the same way in the other direction.
    /// </summary>
    /// <remarks>
    /// Not positive-only, deliberately. <c>[Required]</c> is satisfied by any decimal
    /// though, zero included, so it said nothing here on its own: the dialog opens on
    /// 0.00 and an expense saved straight out of it landed in a shared ledger as a row
    /// that moved nobody's balance and that nobody could account for.
    /// </remarks>
    /// <summary>
    /// The ISO 4217 code the amount is in, or null for the group's own -- or, for a
    /// personal expense, the default.
    /// </summary>
    /// <remarks>
    /// Stated for a personal expense, which may be in any currency: nothing adds it to
    /// anybody else's money, so a dinner abroad can be written down in what it cost. In a
    /// group it can only be the group's own, because a group keeps its balances in one
    /// currency and conversion is out of scope -- any other is refused with
    /// <c>CURRENCY_MISMATCH</c> rather than relabelled. The currency is fixed once the
    /// expense exists, which is why the edit request has no such field.
    /// </remarks>
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.CurrencyCode))]
    public string? Currency { get; set; }

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountRequired))]
    [NotZero(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountNotZero))]
    [MaxDecimalPlaces(2, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountPrecision))]
    public decimal Amount { get; set; }

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DateRequired))]
    [NotDefault(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DateRequired))]
    public DateTimeOffset DateTime { get; set; }
};

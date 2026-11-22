using System.ComponentModel.DataAnnotations;
using GroupSplit.Shared.CustomValidationAttributes;

namespace GroupSplit.Shared;

/// <summary>
/// An expense that repeats: what each occurrence is, and when the occurrences fall.
/// </summary>
/// <remarks>
/// The expense fields mean what they mean on <see cref="CreateTransactionRequest"/>, which each
/// occurrence is created with, dated on its day. There is no date and no receipt: every
/// occurrence gets its own day, and a receipt belongs to one purchase.
/// </remarks>
public record RecurringExpenseRequest
{
    /// <summary>The group the occurrences are recorded in, or null for the caller's own.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Who pays each occurrence, or null for the caller.</summary>
    public Guid? PaidByUserId { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? MerchantId { get; set; }

    public Guid? SplitRuleId { get; set; }

    public IReadOnlyList<SplitInput>? Splits { get; set; }

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameRequired))]
    [StringLength(TransactionNames.MaxLength, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.NameLength))]
    public string Name { get; set; } = null!;

    [StringLength(256, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DescriptionShort))]
    public string? Description { get; set; }

    [RegularExpression("^[A-Za-z]{3}$", ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.CurrencyCode))]
    public string? Currency { get; set; }

    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountRequired))]
    [NotZero(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountNotZero))]
    [MaxDecimalPlaces(2, ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.AmountPrecision))]
    public decimal Amount { get; set; }

    public RepeatFrequency Frequency { get; set; } = RepeatFrequency.Monthly;

    /// <summary>
    /// The first occurrence's day, which also fixes the day of the month (or of the week, or
    /// of the year) the later ones fall on.
    /// </summary>
    [Required(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DateRequired))]
    [NotDefault(ErrorMessageResourceType = typeof(GroupSplit.Shared.Localization.ValidationMessages), ErrorMessageResourceName = nameof(GroupSplit.Shared.Localization.ValidationMessages.DateRequired))]
    public DateOnly StartsOn { get; set; }

    /// <summary>The expense an occurrence on <paramref name="day"/> is created with.</summary>
    public CreateTransactionRequest On(DateOnly day) => new()
    {
        GroupId = GroupId,
        PaidByUserId = PaidByUserId,
        CategoryId = CategoryId,
        MerchantId = MerchantId,
        SplitRuleId = SplitRuleId,
        Splits = Splits,
        Name = Name,
        Description = Description,
        Currency = Currency,
        Amount = Amount,
        // Midday UTC, so the day reads the same in every time zone people are in.
        DateTime = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)
    };
}

/// <summary>Turn an existing expense into a repeating one, from its next occurrence on.</summary>
/// <param name="From">
/// The expense's own day as the person sees it, which the later ones fall on. Null for its day
/// in UTC, which is the same day except near midnight.
/// </param>
public sealed record RepeatExpenseRequest(RepeatFrequency Frequency, DateOnly? From = null);

using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

/// <summary>
/// Refuses a value type left at its default, which is what an absent member deserialises to.
/// </summary>
/// <remarks>
/// <c>[Required]</c> on a non-nullable value type never fails: a <see cref="DateTimeOffset"/>
/// the request left out arrives as 0001-01-01, which is a value, and was stored as the date
/// of the expense. Making the property nullable would say the same thing louder and move a
/// <c>.Value</c> into every client; this says it where the annotation already is.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public class NotDefaultAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is null)
            return ValidationResult.Success;

        var type = value.GetType();

        if (!type.IsValueType || !value.Equals(Activator.CreateInstance(type)))
            return ValidationResult.Success;

        return new ValidationResult(ErrorMessage,
            context.MemberName is { } member ? [member] : null);
    }
}

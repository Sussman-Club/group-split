using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

/// <summary>
/// Refuses zero, in both directions.
/// </summary>
/// <remarks>
/// <see cref="GreaterThanAttribute"/> is the usual way to say an amount has to mean
/// something, and it is right for a repayment: money moves one way there. It is wrong for
/// an expense, which can be negative -- a refund divides the same way an expense does,
/// back the other way. Zero is the only figure neither reading has a use for.
/// </remarks>
public class NotZeroAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is not decimal dec)
            return ValidationResult.Success;

        return dec == 0m ? new ValidationResult(ErrorMessage) : ValidationResult.Success;
    }
}

using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

public class GreaterThanAttribute : ValidationAttribute
{
    private readonly decimal _min;

    public GreaterThanAttribute(double min)
    {
        _min = (decimal)min;
        ErrorMessage = $"Value must be greater than {_min}.";
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is not decimal dec) return new ValidationResult(ErrorMessage);
        return dec > _min ? ValidationResult.Success : new ValidationResult(ErrorMessage);
    }
}
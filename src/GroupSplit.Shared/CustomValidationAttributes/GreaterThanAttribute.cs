using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

public class GreaterThanAttribute : ValidationAttribute
{
    private readonly decimal _min;

    public GreaterThanAttribute(double min)
    {
        _min = (decimal)min;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is not decimal dec) return new ValidationResult(FormatErrorMessage(context.DisplayName));
        return dec > _min ? ValidationResult.Success : new ValidationResult(FormatErrorMessage(context.DisplayName));
    }

    public override string FormatErrorMessage(string name) =>
        ErrorMessage is not null || ErrorMessageResourceType is not null
            ? base.FormatErrorMessage(name)
            : $"Value must be greater than {_min}.";
}

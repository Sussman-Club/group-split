using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace GroupSplit.Shared.CustomValidationAttributes;

public class DecimalPrecisionAttribute(int precision, int scale) : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not decimal dec)
            return ValidationResult.Success;

        var str = dec.ToString(CultureInfo.InvariantCulture);

        var parts = str.Split('.');
        var wholeDigits = parts[0].TrimStart('-').Length;
        var decimalDigits = parts.Length > 1 ? parts[1].Length : 0;

        if (wholeDigits + decimalDigits <= precision && decimalDigits <= scale)
            return ValidationResult.Success;

        return new ValidationResult(ErrorMessage);
    }
}
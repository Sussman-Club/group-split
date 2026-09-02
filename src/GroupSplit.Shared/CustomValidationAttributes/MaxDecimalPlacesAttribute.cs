using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

public class MaxDecimalPlacesAttribute : ValidationAttribute
{
    private readonly int _decimalPlaces;

    public MaxDecimalPlacesAttribute(int decimalPlaces)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);
        _decimalPlaces = decimalPlaces;
    }

    public override bool IsValid(object? value)
    {
        if (value is not decimal dec)
            return true;

        var power = (decimal)Math.Pow(10, _decimalPlaces);
        var truncated = decimal.Truncate(dec * power) / power;
        return truncated == dec;
    }
}
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

        try
        {
            var power = (decimal)Math.Pow(10, _decimalPlaces);
            var truncated = decimal.Truncate(dec * power) / power;
            return truncated == dec;
        }
        catch (OverflowException)
        {
            // The scale is applied by multiplying, so a value large enough that
            // value * 10^places leaves decimal's range used to throw straight out of
            // validation -- which reaches the client as a 500 for what is a rejected
            // input. A value that cannot even be scaled does not satisfy the limit, so
            // it fails validation like any other bad amount and the caller gets a 400.
            return false;
        }
    }
}
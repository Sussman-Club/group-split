using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

public class DecimalScaleAttribute : ValidationAttribute
{
    private readonly int _scale;

    public DecimalScaleAttribute(int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        _scale = scale;
    }

    public override bool IsValid(object? value)
    {
        if (value is not decimal dec)
            return true;

        var bits = decimal.GetBits(dec);
        var actualScale = (bits[3] >> 16) & 0xFF;
        return actualScale == _scale;
    }
}
using System.Collections;
using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

public class NonNegativeValuesAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is not IDictionary dict)
            return true;

        foreach (var entry in dict.Values)
        {
            if (entry is IComparable comparable && comparable.CompareTo(0) < 0)
                return false;
        }

        return true;
    }
}
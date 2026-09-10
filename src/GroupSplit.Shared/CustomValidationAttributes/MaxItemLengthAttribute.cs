using System.Collections;
using System.ComponentModel.DataAnnotations;

namespace GroupSplit.Shared.CustomValidationAttributes;

/// <summary>
/// Bounds the length of each string in a collection, where <see cref="StringLengthAttribute"/>
/// bounds only a single one.
/// </summary>
/// <remarks>
/// A list of strings on the wire has nothing to hang a per-item rule from: the annotations
/// apply to the property, and the property is the list. So the values reach the database and
/// are rejected there, which turns a wrong input into a 500 with a trace id -- the same
/// failure <see cref="MaxDecimalPlacesAttribute"/> exists to keep out of the amount fields.
/// <para>
/// Null and empty entries pass. Whether a list needs a value at all, and what an entry of
/// nothing means, are the caller's rules and not this one's: <c>InviteToGroupRequest</c>
/// trims and drops blanks, and refuses only a request that named nobody at all.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class MaxItemLengthAttribute : ValidationAttribute
{
    private readonly int _length;

    public MaxItemLengthAttribute(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
    }

    public override bool IsValid(object? value)
    {
        if (value is not IEnumerable items || value is string)
            return true;

        foreach (var item in items)
        {
            if (item is string text && text.Trim().Length > _length)
                return false;
        }

        return true;
    }

    public override string FormatErrorMessage(string name) =>
        $"Each entry in {name} must be {_length} characters or fewer.";
}

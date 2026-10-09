using System.Globalization;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Utilities.Exceptions;

namespace GroupSplit.App.Shared.Components;

/// <summary>
/// The field every amount, share, percentage and quantity in the app is typed into: a
/// <see cref="MudNumericField{T}"/> that reads what was typed with <see cref="DecimalText"/>
/// instead of with a culture.
/// </summary>
/// <remarks>
/// MudBlazor parses a numeric field with the invariant culture unless told otherwise, and
/// the invariant culture reads a comma as a thousands separator -- so <c>12,50</c>, typed on
/// any phone whose keypad offers a comma for its decimal key, was stored as 1250 (#313). A
/// culture cannot fix that on its own: every culture treats one of the two separators as
/// grouping and drops it. Here a comma or a dot is the decimal point and nothing is ever
/// grouping, so <c>12,50</c> is 12.50 and <c>1.234,56</c> is refused rather than guessed.
/// <para>
/// A component rather than a converter each field has to remember to set, so a new money
/// field cannot quietly go back to the old parse; <c>DecimalFieldTest</c> fails if a
/// <c>MudNumericField</c> for anything but a whole number appears in the app again.
/// </para>
/// </remarks>
/// <typeparam name="T"><see cref="decimal"/> or <see cref="Nullable{T}"/> of it.</typeparam>
public class DecimalField<T> : MudNumericField<T>
{
    /// <summary>
    /// The most decimal places the value may have. Two, for money, shares and percentages;
    /// a receipt line's quantity holds three and its tax rate four.
    /// </summary>
    [Parameter]
    public int Places { get; set; } = DecimalText.MoneyPlaces;

    public DecimalField()
    {
        if (typeof(T) != typeof(decimal) && typeof(T) != typeof(decimal?))
            throw new InvalidOperationException($"{nameof(DecimalField<T>)} holds a decimal, not a {typeof(T).Name}.");
    }

    /// <summary>
    /// Always a format, which is also what keeps MudBlazor reformatting the text once it is
    /// read -- so <c>12,5</c> becomes <c>12.50</c> on leaving the field, and the person sees
    /// the number that will be stored. Never grouped: a grouped figure shown back in the field
    /// would be text this field refuses the moment somebody edits it.
    /// </summary>
    protected override string GetFormat()
        => "0.00" + new string('#', Math.Max(0, Places - 2));

    protected override IConverter<T?, string?> GetDefaultConverter() => new TextConverter(this);

    private sealed class TextConverter(DecimalField<T> field) : IReversibleConverter<T?, string?>
    {
        public string? Convert(T? input)
            => input is decimal value ? value.ToString(field.GetFormat(), CultureInfo.InvariantCulture) : null;

        public T? ConvertBack(string? input)
        {
            // Cleared is no amount: null where the model can hold that, zero where it cannot,
            // which is what MudBlazor's own converter did before this one.
            if (string.IsNullOrWhiteSpace(input))
                return default;

            if (!DecimalText.TryParse(input, field.Places, commaIsDecimal: true, out var value, out var problem))
                throw new ConversionException(problem);

            return (T)(object)value;
        }
    }
}

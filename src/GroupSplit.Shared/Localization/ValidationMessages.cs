using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace GroupSplit.Shared.Localization;

/// <summary>Validation resources shared by client forms and request contracts.</summary>
public static class ValidationMessages
{
    private static readonly ResourceManager Resources = new(typeof(ValidationMessages));

    /// <summary>Localizes known API validation templates while preserving unknown server messages.</summary>
    public static string Translate(string message)
    {
        var english = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        foreach (System.Collections.DictionaryEntry entry in english)
        {
            var template = (string)entry.Value!;
            var pattern = Regex.Replace(Regex.Escape(template), @"\\\{(\d+)}",
                match => $"(?<arg{match.Groups[1].Value}>[0-9]+)");
            var found = Regex.Match(message, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
            if (!found.Success) continue;
            var translated = Resources.GetString((string)entry.Key, CultureInfo.CurrentUICulture)!;
            return Regex.Replace(translated, @"\{(\d+)\}",
                match => found.Groups["arg" + match.Groups[1].Value].Value);
        }
        return message;
    }
    public static string NameRequired => Resources.GetString(nameof(NameRequired), CultureInfo.CurrentUICulture)!;
    public static string NameRequiredShort => Resources.GetString(nameof(NameRequiredShort), CultureInfo.CurrentUICulture)!;
    public static string NameLength64 => Resources.GetString(nameof(NameLength64), CultureInfo.CurrentUICulture)!;
    public static string NameLength128 => Resources.GetString(nameof(NameLength128), CultureInfo.CurrentUICulture)!;
    public static string NameLength => Resources.GetString(nameof(NameLength), CultureInfo.CurrentUICulture)!;
    public static string DescriptionLength => Resources.GetString(nameof(DescriptionLength), CultureInfo.CurrentUICulture)!;
    public static string DescriptionShort => Resources.GetString(nameof(DescriptionShort), CultureInfo.CurrentUICulture)!;
    public static string LogoLength => Resources.GetString(nameof(LogoLength), CultureInfo.CurrentUICulture)!;
    public static string RowsLength => Resources.GetString(nameof(RowsLength), CultureInfo.CurrentUICulture)!;
    public static string ReceiptItemsLength => Resources.GetString(nameof(ReceiptItemsLength), CultureInfo.CurrentUICulture)!;
    public static string GuestRequired => Resources.GetString(nameof(GuestRequired), CultureInfo.CurrentUICulture)!;
    public static string InviteRequired => Resources.GetString(nameof(InviteRequired), CultureInfo.CurrentUICulture)!;
    public static string GuestNameLength => Resources.GetString(nameof(GuestNameLength), CultureInfo.CurrentUICulture)!;
    public static string UtcOffset => Resources.GetString(nameof(UtcOffset), CultureInfo.CurrentUICulture)!;
    public static string DefaultPeriod => Resources.GetString(nameof(DefaultPeriod), CultureInfo.CurrentUICulture)!;
    public static string AmountRequired => Resources.GetString(nameof(AmountRequired), CultureInfo.CurrentUICulture)!;
    public static string AmountPrecision => Resources.GetString(nameof(AmountPrecision), CultureInfo.CurrentUICulture)!;
    public static string AmountPositive => Resources.GetString(nameof(AmountPositive), CultureInfo.CurrentUICulture)!;
    public static string AmountNotZero => Resources.GetString(nameof(AmountNotZero), CultureInfo.CurrentUICulture)!;
    public static string DateRequired => Resources.GetString(nameof(DateRequired), CultureInfo.CurrentUICulture)!;
    public static string CurrencyCode => Resources.GetString(nameof(CurrencyCode), CultureInfo.CurrentUICulture)!;
    public static string SharePrecision => Resources.GetString(nameof(SharePrecision), CultureInfo.CurrentUICulture)!;
}

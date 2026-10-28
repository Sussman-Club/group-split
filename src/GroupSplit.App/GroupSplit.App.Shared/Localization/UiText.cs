using System.Globalization;
using System.Resources;

namespace GroupSplit.App.Shared.Localization;

/// <summary>Resource access for static C# copy helpers. Razor components use their injected localizer.</summary>
public static class UiText
{
    private static readonly ResourceManager Resources = new(typeof(AppStrings));

    public static string Get(string english) => Resources.GetString(english, CultureInfo.CurrentUICulture) ?? english;

    public static string Format(FormattableString sentence) =>
        string.Format(CultureInfo.CurrentCulture, Get(sentence.Format), sentence.GetArguments());
}

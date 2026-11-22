using GroupSplit.App.Shared.Localization;
using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Extensions;

/// <summary>How often a repeating expense comes round, in words (#546).</summary>
public static class RepeatCopy
{
    public static string Every(RepeatFrequency frequency) => frequency switch
    {
        RepeatFrequency.Weekly => UiText.Get("Every week"),
        RepeatFrequency.Yearly => UiText.Get("Every year"),
        _ => UiText.Get("Every month")
    };
}

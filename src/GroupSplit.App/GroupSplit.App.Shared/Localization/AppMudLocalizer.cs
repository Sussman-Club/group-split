using Microsoft.Extensions.Localization;
using MudBlazor;

namespace GroupSplit.App.Shared.Localization;

/// <summary>MudBlazor falls back to its English catalog for keys absent from ours.</summary>
public sealed class AppMudLocalizer(IStringLocalizer<AppStrings> strings) : MudLocalizer
{
    public override LocalizedString this[string key] => strings[key];
    public override LocalizedString this[string key, params object[] arguments] => strings[key, arguments];
}

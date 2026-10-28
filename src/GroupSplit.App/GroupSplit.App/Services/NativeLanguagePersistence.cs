using System.Globalization;
using GroupSplit.App.Shared.Localization;
using GroupSplit.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Maui.Storage;

namespace GroupSplit.App.Services;

public sealed class NativeLanguagePersistence(IPreferences preferences, NavigationManager navigation) : ILanguagePersistence
{
    public const string StorageKey = "groupsplit.language";

    public static void Initialize(IPreferences preferences)
    {
        var language = preferences.Get(StorageKey, CultureInfo.CurrentUICulture.Name);
        var culture = CultureInfo.GetCultureInfo(AppLanguages.Resolve(language));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public Task ApplyAsync(string language, CancellationToken ct = default)
    {
        preferences.Set(StorageKey, language);
        Initialize(preferences);
        navigation.Refresh(forceReload: true);
        return Task.CompletedTask;
    }
}

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GroupSplit.App.Shared.Localization;

/// <summary>The host persists culture, then recreates the app with that culture.</summary>
public interface ILanguagePersistence
{
    Task ApplyAsync(string language, CancellationToken ct = default);
}

public sealed class BrowserLanguagePersistence(IJSRuntime js, NavigationManager navigation) : ILanguagePersistence
{
    public async Task ApplyAsync(string language, CancellationToken ct = default)
    {
        await js.InvokeVoidAsync("gs.language.save", ct, language);
        navigation.Refresh(forceReload: true);
    }
}

using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Localization;

public sealed class LanguagePreference(IUsersClient users, ILanguagePersistence persistence)
{
    public async Task SaveAsync(string language, CancellationToken ct = default)
    {
        if (!AppLanguages.IsSupported(language)) throw new ArgumentException("Unsupported language.", nameof(language));
        // Save the account first: a refused write must not change this device's language.
        await users.SetCurrentUserLanguageAsync(new LanguagePreferenceRequest(language), ct);
        await persistence.ApplyAsync(language, ct);
    }
}

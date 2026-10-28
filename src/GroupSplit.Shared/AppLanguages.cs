namespace GroupSplit.Shared;

/// <summary>Language tags stored on accounts. Regional browser tags resolve to their language.</summary>
public static class AppLanguages
{
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(new[] { "en", "es", "pt" });

    public static bool IsSupported(string? language) => language is not null && Supported.Contains(language);

    public static string Resolve(string? language)
    {
        var neutral = language?.Split('-', '_')[0].ToLowerInvariant();
        return IsSupported(neutral) ? neutral! : "en";
    }
}

public record LanguagePreferenceRequest(string Language);

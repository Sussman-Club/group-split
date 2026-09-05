namespace GroupSplit.App.Shared.Extensions;

public static class NameExtensions
{
    /// <summary>Up to two initials from a display name, e.g. "Anabel Benítez" → "AB".</summary>
    public static string Initials(this string? name) =>
        string.Concat(
            (name ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0])));

    /// <summary>
    /// A stable avatar tint for a name, so the same person always gets the
    /// same colour across the app without storing anything.
    /// </summary>
    public static string AvatarTone(this string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        var hash = 0;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7fffffff;

        return (hash % 3) switch
        {
            1 => "clay",
            2 => "ink",
            _ => ""
        };
    }
}

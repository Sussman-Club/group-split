using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Extensions;

public static class NameExtensions
{
    /// <summary>
    /// What to call somebody in a list of a group's people, saying so when they have been
    /// invited and have not joined.
    /// </summary>
    /// <remarks>
    /// One helper rather than a marker per screen, because the marker has to be everywhere
    /// a person can be chosen -- who paid, whose share, who a rule names -- and a screen
    /// that forgot it would be offering an invitee as though they were a member. The name
    /// itself falls back to the address, which is all a group knows about somebody who has
    /// never signed in; see <see cref="UserInfo.FullName"/>.
    /// </remarks>
    public static string Label(this UserInfo? person) =>
        person is null
            ? string.Empty
            : person.IsPendingInvitee
                ? $"{person.FullName} (invited)"
                : person.FullName;

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

using Microsoft.AspNetCore.Components;

namespace GroupSplit.App.Shared.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>Where a theme change came from, and so whether it is worth writing back.</summary>
public enum ThemeChangeOrigin
{
    /// <summary>The browser told us: the stored choice was restored, or the OS preference moved.</summary>
    Browser,

    /// <summary>Somebody picked a mode. The only origin worth persisting.</summary>
    Choice
}

/// <summary>
/// Holds the chosen theme mode and the last known system preference, and
/// resolves the two into the dark/light flag the theme provider consumes.
/// Deliberately free of JS interop so it can be set during prerender.
/// </summary>
/// <remarks>
/// A fresh instance answers light, and for a long time that is what the first render
/// showed everybody: the prerender has no browser to ask, and the runtime that replaced
/// it started from a fresh instance too, so a dark-mode user watched Mud's colours go
/// light and back on every page load. Two things now put the answer in before anything
/// renders. The web host seeds the prerender's instance from a cookie that gs.js keeps
/// beside the stored choice; and the two properties below are persisted from that
/// prerender into the runtime, so its first render draws what the prerender drew. The
/// browser's own answer still arrives afterwards and still wins -- it just has nothing
/// to change, most of the time.
/// </remarks>
public sealed class ThemePreference
{
    public const string StorageKey = "groupsplit.theme";

    /// <summary>
    /// The chosen mode. Settable for the seeding and persistence described above, which
    /// happen before anybody is listening; a change worth announcing goes through
    /// <see cref="Set"/>.
    /// </summary>
    [PersistentState]
    public ThemeMode Mode { get; set; } = ThemeMode.System;

    /// <summary>
    /// The OS preference as last reported. Settable for the same reason as
    /// <see cref="Mode"/>; a change worth announcing goes through <see cref="SetSystemIsDark"/>.
    /// </summary>
    [PersistentState]
    public bool SystemIsDark { get; set; }

    public bool IsDark => Mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => SystemIsDark
    };

    public event Action<ThemeChangeOrigin>? Changed;

    /// <summary>
    /// Adopts the two answers only a browser can give -- the stored choice and
    /// the OS preference -- as one change.
    ///
    /// Both have to land before anything observes the result, which is why this
    /// is one method rather than two setters. Applying the OS preference first
    /// and the stored choice second left the pair resolving to dark in between
    /// for the commonest case there is, a dark-desktop user who asked for Light:
    /// the mode was still <see cref="ThemeMode.System"/> at that point, so the
    /// dark desktop won. The gap between the two was an interop call, which is
    /// long enough for the browser to paint the dark frame.
    /// </summary>
    public void Hydrate(ThemeMode mode, bool systemIsDark)
    {
        var wasMode = Mode;
        var wasDark = IsDark;

        Mode = mode;
        SystemIsDark = systemIsDark;

        // The menu renders the mode and the provider renders the resolved flag,
        // so either one moving is worth announcing.
        if (Mode != wasMode || IsDark != wasDark)
        {
            Changed?.Invoke(ThemeChangeOrigin.Browser);
        }
    }

    public void Set(ThemeMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        Changed?.Invoke(ThemeChangeOrigin.Choice);
    }

    /// <summary>Records the OS preference, which only matters in <see cref="ThemeMode.System"/>.</summary>
    public void SetSystemIsDark(bool isDark)
    {
        if (SystemIsDark == isDark)
        {
            return;
        }

        SystemIsDark = isDark;

        if (Mode == ThemeMode.System)
        {
            Changed?.Invoke(ThemeChangeOrigin.Browser);
        }
    }

    public static ThemeMode Parse(string? value) => value switch
    {
        nameof(ThemeMode.Light) => ThemeMode.Light,
        nameof(ThemeMode.Dark) => ThemeMode.Dark,
        _ => ThemeMode.System
    };
}

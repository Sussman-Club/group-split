namespace GroupSplit.App.Shared.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// Holds the chosen theme mode and the last known system preference, and
/// resolves the two into the dark/light flag the theme provider consumes.
/// Deliberately free of JS interop so it can be set during prerender.
/// </summary>
public sealed class ThemePreference
{
    public const string StorageKey = "groupsplit.theme";

    private bool _systemIsDark;

    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    public bool IsDark => Mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => _systemIsDark
    };

    public event Action? Changed;

    public void Set(ThemeMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        Changed?.Invoke();
    }

    /// <summary>Records the OS preference, which only matters in <see cref="ThemeMode.System"/>.</summary>
    public void SetSystemIsDark(bool isDark)
    {
        if (_systemIsDark == isDark)
        {
            return;
        }

        _systemIsDark = isDark;

        if (Mode == ThemeMode.System)
        {
            Changed?.Invoke();
        }
    }

    public static ThemeMode Parse(string? value) => value switch
    {
        nameof(ThemeMode.Light) => ThemeMode.Light,
        nameof(ThemeMode.Dark) => ThemeMode.Dark,
        _ => ThemeMode.System
    };
}

namespace GroupSplit.Cli.Configuration;

/// <summary>
/// Where the CLI keeps its two files. Config and credentials are deliberately separate:
/// the first is worth committing to a dotfiles repo, the second never is.
/// </summary>
public static class ConfigPaths
{
    private const string AppFolder = "groupsplit";

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public static string CredentialsFile => Path.Combine(DataDirectory, "credentials.json");

    /// <summary>XDG on Unix, %APPDATA% on Windows.</summary>
    public static string ConfigDirectory =>
        Path.Combine(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(EnvironmentVariables.ConfigHome),
                OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Path.Combine(Home, ".config")),
            AppFolder);

    public static string DataDirectory =>
        Path.Combine(
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(EnvironmentVariables.DataHome),
                OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Path.Combine(Home, ".local", "share")),
            AppFolder);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string FirstNonEmpty(string? preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}

using System.Reflection;

namespace GroupSplit.Cli.Infrastructure;

public static class CliVersion
{
    /// <summary>
    /// The informational version, trimmed of the source-revision suffix the SDK appends,
    /// so it reads as a version rather than as a commit hash.
    /// </summary>
    public static string Value { get; } =
        typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";
}

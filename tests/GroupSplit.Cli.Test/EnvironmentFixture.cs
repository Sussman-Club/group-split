using GroupSplit.Cli.Configuration;

namespace GroupSplit.Cli.Test;

/// <summary>
/// Points the CLI's config and credential paths at a scratch directory and clears every
/// GROUPSPLIT_* variable, so a test sees a clean machine rather than the developer's own
/// sign-in. Environment variables are process-wide, which is why everything using this
/// runs in one non-parallel collection.
/// </summary>
public sealed class EnvironmentFixture : IDisposable
{
    private static readonly string[] Managed =
    [
        EnvironmentVariables.Server,
        EnvironmentVariables.ApiUrl,
        EnvironmentVariables.Authority,
        EnvironmentVariables.ClientId,
        EnvironmentVariables.Profile,
        EnvironmentVariables.Token,
        EnvironmentVariables.ConfigHome,
        EnvironmentVariables.DataHome,
        "NO_COLOR"
    ];

    private readonly Dictionary<string, string?> _original = [];

    public EnvironmentFixture()
    {
        foreach (var name in Managed)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        Root = Directory.CreateTempSubdirectory("groupsplit-cli-test").FullName;

        Environment.SetEnvironmentVariable(EnvironmentVariables.ConfigHome, Path.Combine(Root, "config"));
        Environment.SetEnvironmentVariable(EnvironmentVariables.DataHome, Path.Combine(Root, "data"));
    }

    public string Root { get; }

    public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "environment";
}

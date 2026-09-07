using System.Text.Json;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Configuration;

/// <summary>Reads and writes <see cref="ConfigPaths.ConfigFile"/>.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string Path => ConfigPaths.ConfigFile;

    public CliConfigFile Load()
    {
        if (!File.Exists(Path))
        {
            return new CliConfigFile();
        }

        try
        {
            return JsonSerializer.Deserialize<CliConfigFile>(File.ReadAllText(Path), Options)
                   ?? new CliConfigFile();
        }
        catch (JsonException ex)
        {
            throw CliException.Failure(
                $"The config file at {Path} is not valid JSON.",
                ErrorCodes.ConfigError,
                $"Fix the file by hand, or delete it and run: groupsplit config set server <url>",
                inner: ex);
        }
    }

    public void Save(CliConfigFile config)
    {
        Directory.CreateDirectory(ConfigPaths.ConfigDirectory);
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Options));
    }
}

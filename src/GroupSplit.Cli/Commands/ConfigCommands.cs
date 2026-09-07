using System.CommandLine;
using GroupSplit.Cli.Configuration;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

public static class ConfigCommands
{
    private const string ServerKey = "server";
    private const string ApiUrlKey = "apiurl";
    private const string AuthorityKey = "authority";
    private const string ClientIdKey = "clientid";

    private static readonly string[] Keys = [ServerKey, ApiUrlKey, AuthorityKey, ClientIdKey];

    private static readonly Argument<string> Key = new("key")
    {
        Description = "One of: server, apiUrl, authority, clientId."
    };

    public static Command Build()
    {
        var config = new Command("config",
            "Read and write the stored server settings. Nothing here is baked into the binary.");

        config.Subcommands.Add(List());
        config.Subcommands.Add(Get());
        config.Subcommands.Add(Set());
        config.Subcommands.Add(Unset());
        config.Subcommands.Add(Profiles());
        config.Subcommands.Add(Path());

        return config;
    }

    private static Command List()
    {
        var command = new Command("list", "Show the settings this invocation would use, and where each came from.");

        command.SetHandler((context, _) =>
        {
            // Resolve rather than dump the file: the answer a caller wants is what the
            // next command will actually talk to, after flags and environment are applied.
            var endpoints = context.Endpoints;

            var payload = new
            {
                profile = endpoints.ProfileName,
                api = endpoints.Api.ToString(),
                authority = endpoints.AuthorityOrNull?.ToString(),
                clientId = endpoints.ClientId,
                source = endpoints.Source,
                configFile = context.Config.Path,
                credentialsFile = context.Tokens.Path
            };

            context.Output.Write(payload, value =>
            {
                var table = Tables.KeyValue();
                table.AddRow("Profile", Markup.Escape(value.profile));
                table.AddRow("API", Markup.Escape(value.api));
                table.AddRow("Authority", Markup.Escape(value.authority ?? "-"));
                table.AddRow("Client", Markup.Escape(value.clientId));
                table.AddRow("Source", Markup.Escape(value.source));
                table.AddRow("Config", Markup.Escape(value.configFile));
                table.AddRow("Credentials", Markup.Escape(value.credentialsFile));

                return table;
            });

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static Command Get()
    {
        var command = new Command("get", "Print one stored setting.") { Key };

        command.SetHandler((context, _) =>
        {
            var key = Normalize(context.ParseResult.GetValue(Key));
            var profile = ReadProfile(context, out var name);
            var value = Read(profile, key);

            context.Output.Write(
                new { profile = name, key, value },
                result => new Markup(Markup.Escape(result.value ?? string.Empty) + "\n"));

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static Command Set()
    {
        var value = new Argument<string>("value") { Description = "The value to store." };
        var command = new Command("set", "Store one setting in the current profile.") { Key, value };

        command.SetHandler((context, _) =>
        {
            var key = Normalize(context.ParseResult.GetValue(Key));
            var newValue = context.ParseResult.GetValue(value)!;

            var config = context.Config.Load();
            var name = ProfileName(context, config);

            if (!config.Profiles.TryGetValue(name, out var profile))
            {
                config.Profiles[name] = profile = new CliProfile();
            }

            Write(profile, key, newValue);
            context.Config.Save(config);

            context.Output.Write(
                new { profile = name, key, value = newValue, configFile = context.Config.Path },
                result => new Markup(
                    $"Set [bold]{Markup.Escape(result.key)}[/] on profile "
                    + $"[bold]{Markup.Escape(result.profile)}[/] in {Markup.Escape(result.configFile)}\n"));

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static Command Unset()
    {
        var command = new Command("unset", "Remove one setting from the current profile.") { Key };

        command.SetHandler((context, _) =>
        {
            var key = Normalize(context.ParseResult.GetValue(Key));
            var config = context.Config.Load();
            var name = ProfileName(context, config);

            if (config.Profiles.TryGetValue(name, out var profile))
            {
                Write(profile, key, null);
                context.Config.Save(config);
            }

            context.Output.Write(
                new { profile = name, key, value = (string?)null },
                result => new Markup($"Unset [bold]{Markup.Escape(result.key)}[/].\n"));

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static Command Profiles()
    {
        var command = new Command("profiles", "List the configured profiles.");

        command.SetHandler((context, _) =>
        {
            var config = context.Config.Load();

            var payload = config.Profiles
                .Select(entry => new
                {
                    name = entry.Key,
                    isDefault = string.Equals(entry.Key, config.DefaultProfile, StringComparison.OrdinalIgnoreCase),
                    server = entry.Value.Server,
                    apiUrl = entry.Value.ApiUrl,
                    authority = entry.Value.Authority
                })
                .ToList();

            context.Output.Write(payload, value =>
            {
                if (value.Count == 0)
                {
                    return new Markup(Tables.Empty("profiles configured") + "\n");
                }

                var table = Tables.Grid("Profile", "Server", "API override", "Authority override");

                foreach (var profile in value)
                {
                    table.AddRow(
                        Markup.Escape(profile.name) + (profile.isDefault ? " [grey](default)[/]" : string.Empty),
                        Markup.Escape(profile.server ?? "-"),
                        Markup.Escape(profile.apiUrl ?? "-"),
                        Markup.Escape(profile.authority ?? "-"));
                }

                return table;
            });

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static Command Path()
    {
        var command = new Command("path", "Print the path of the config file.");

        command.SetHandler((context, _) =>
        {
            context.Output.Write(
                new { configFile = context.Config.Path, credentialsFile = context.Tokens.Path },
                value => new Markup(Markup.Escape(value.configFile) + "\n"));

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static string ProfileName(CliContext context, CliConfigFile config)
        => context.ParseResult.GetValue(GlobalOptions.Profile)
           ?? Environment.GetEnvironmentVariable(EnvironmentVariables.Profile)
           ?? config.DefaultProfile;

    private static CliProfile? ReadProfile(CliContext context, out string name)
    {
        var config = context.Config.Load();
        name = ProfileName(context, config);

        return config.Profiles.GetValueOrDefault(name);
    }

    private static string Normalize(string? key)
    {
        var normalized = key?.ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);

        if (normalized is null || !Keys.Contains(normalized))
        {
            throw CliException.Input(
                $"'{key}' is not a config key.",
                "Valid keys: server, apiUrl, authority, clientId");
        }

        return normalized;
    }

    private static string? Read(CliProfile? profile, string key) => key switch
    {
        ServerKey => profile?.Server,
        ApiUrlKey => profile?.ApiUrl,
        AuthorityKey => profile?.Authority,
        ClientIdKey => profile?.ClientId,
        _ => null
    };

    private static void Write(CliProfile profile, string key, string? value)
    {
        switch (key)
        {
            case ServerKey:
                profile.Server = value;
                break;
            case ApiUrlKey:
                profile.ApiUrl = value;
                break;
            case AuthorityKey:
                profile.Authority = value;
                break;
            case ClientIdKey:
                profile.ClientId = value;
                break;
        }
    }
}

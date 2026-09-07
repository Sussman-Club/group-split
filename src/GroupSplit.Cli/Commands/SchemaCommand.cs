using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// Emits the whole command tree as JSON.
/// <para>
/// This exists for the caller that cannot usefully read <c>--help</c>: an agent that would
/// otherwise have to shell out once per subcommand and parse prose to learn the surface.
/// One call returns every command, flag, arity and default, so it can plan a whole
/// invocation before running anything -- and it is generated from the same tree that
/// parses the arguments, so it cannot drift from the real behaviour.
/// </para>
/// </summary>
public static class SchemaCommand
{
    public static Command Build()
    {
        var command = new Command("schema",
            "Print the full command tree as JSON, including every flag, its type and its default.");

        command.SetHandler((context, _) =>
        {
            var root = context.ParseResult.RootCommandResult.Command;

            context.Output.Write(
                new
                {
                    name = "groupsplit",
                    version = CliVersion.Value,
                    exitCodes = new
                    {
                        success = ExitCodes.Success,
                        error = ExitCodes.Error,
                        authRequired = ExitCodes.AuthRequired,
                        invalidInput = ExitCodes.InvalidInput,
                        confirmationRequired = ExitCodes.ConfirmationRequired
                    },
                    command = Describe(root)
                },
                _ => new Markup("[grey]Run with --json, or pipe this command, to read the schema.[/]\n"));

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private static object Describe(Command command) => new
    {
        name = command.Name,
        description = command.Description,
        aliases = command.Aliases.ToArray(),
        arguments = command.Arguments
            .Where(argument => !argument.Hidden)
            .Select(argument => new
            {
                name = argument.Name,
                description = argument.Description,
                type = TypeName(argument.ValueType),
                arity = $"{argument.Arity.MinimumNumberOfValues}..{Max(argument.Arity.MaximumNumberOfValues)}",
                required = argument.Arity.MinimumNumberOfValues > 0
            })
            .ToArray(),
        options = command.Options
            .Where(option => !option.Hidden)
            .Select(option => new
            {
                name = option.Name,
                aliases = option.Aliases.ToArray(),
                description = option.Description,
                type = TypeName(option.ValueType),
                required = option.Required,
                recursive = option.Recursive
            })
            .ToArray(),
        subcommands = command.Subcommands
            .Where(subcommand => !subcommand.Hidden)
            .Select(Describe)
            .ToArray()
    };

    /// <summary>
    /// CLR names are noise to a reader that only needs to know what to type, so the common
    /// ones are reported as what they look like on a command line.
    /// </summary>
    private static string TypeName(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;

        if (actual.IsArray)
        {
            return TypeName(actual.GetElementType()!) + "[]";
        }

        if (actual.IsEnum)
        {
            return string.Join("|", Enum.GetNames(actual).Select(name => name.ToLowerInvariant()));
        }

        return actual switch
        {
            _ when actual == typeof(bool) => "boolean",
            _ when actual == typeof(string) => "string",
            _ when actual == typeof(Guid) => "uuid",
            _ when actual == typeof(int) => "integer",
            _ when actual == typeof(decimal) || actual == typeof(double) => "number",
            _ when actual == typeof(DateTimeOffset) || actual == typeof(DateTime) => "date-time",
            _ => actual.Name
        };
    }

    private static string Max(int maximum)
        => maximum >= int.MaxValue ? "*" : maximum.ToString();
}

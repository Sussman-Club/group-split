using System.CommandLine;
using System.CommandLine.Parsing;
using GroupSplit.Cli.Commands;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;

var root = new RootCommand(
    "GroupSplit from the command line. Every command speaks JSON with --json or when piped.");

GlobalOptions.AddTo(root);

root.Subcommands.Add(AuthCommands.Build());
root.Subcommands.Add(GroupCommands.Build());
root.Subcommands.Add(TransactionCommands.Build());
root.Subcommands.Add(UserCommands.Build());
root.Subcommands.Add(CategoryCommands.Build());
root.Subcommands.Add(SplitRuleCommands.Build());
root.Subcommands.Add(InvitationCommands.Build());
root.Subcommands.Add(ConfigCommands.Build());
root.Subcommands.Add(CompletionCommand.Build());
root.Subcommands.Add(SchemaCommand.Build());

var parseResult = root.Parse(args);

// Parse errors are intercepted rather than left to Invoke, which reports them as prose on
// stderr and exit code 1. A caller that asked for JSON gets the same envelope shape here
// as for every other failure, and a usage mistake is exit code 3 -- distinguishable from
// a command that ran and failed.
if (parseResult.Errors.Count > 0)
{
    var settings = GlobalOptions.ReadOutputSettings(parseResult);

    IOutputWriter output = settings.IsJson
        ? new JsonOutputWriter(settings, Console.Out, Console.Error)
        : new TextOutputWriter(settings, Console.Out, Console.Error);

    output.WriteError(new CliError(
        parseResult.Errors[0].Message,
        ErrorCodes.Usage,
        $"Run: groupsplit {CommandPath(parseResult)}--help",
        parseResult.Errors.Select(error => error.Message).ToList()));

    return ExitCodes.InvalidInput;
}

return await parseResult.InvokeAsync();

// The deepest command that did parse, so the hint points at the help that covers the
// mistake rather than at the root. Walks the result tree rather than the symbol tree,
// because a subcommand's symbol can be reached from more than one parent.
static string CommandPath(ParseResult parseResult)
{
    var names = new List<string>();

    for (CommandResult? result = parseResult.CommandResult;
         result?.Parent is not null;
         result = result.Parent as CommandResult)
    {
        names.Insert(0, result.Command.Name);
    }

    return names.Count == 0 ? string.Empty : string.Join(' ', names) + " ";
}

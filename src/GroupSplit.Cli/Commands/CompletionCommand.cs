using System.CommandLine;
using GroupSplit.Cli.Infrastructure;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// Prints a shell script that wires tab completion to the <c>[suggest]</c> directive
/// System.CommandLine already answers. The scripts are thin on purpose: the command tree
/// is the single source of truth, so a new subcommand completes without anyone
/// remembering to update a static list of words here.
/// </summary>
public static class CompletionCommand
{
    private enum Shell
    {
        Bash,
        Zsh,
        Fish,
        Pwsh
    }

    public static Command Build()
    {
        var shell = new Argument<Shell>("shell") { Description = "bash, zsh, fish or pwsh." };

        var command = new Command("completion", "Print a shell completion script.") { shell };

        command.SetHandler((context, _) =>
        {
            // Straight to stdout, unformatted: the output of this command is meant to be
            // evaluated by a shell, so a table or a JSON envelope would be unusable.
            Console.Out.WriteLine(context.ParseResult.GetValue(shell) switch
            {
                Shell.Bash => Bash,
                Shell.Zsh => Zsh,
                Shell.Fish => Fish,
                Shell.Pwsh => Pwsh,
                _ => throw CliException.Input("Unknown shell.", "Use one of: bash, zsh, fish, pwsh")
            });

            return Task.FromResult(ExitCodes.Success);
        });

        return command;
    }

    private const string Bash = """
        # groupsplit completion. Install with:
        #   groupsplit completion bash > /etc/bash_completion.d/groupsplit
        # or, for the current user:
        #   echo 'source <(groupsplit completion bash)' >> ~/.bashrc
        _groupsplit_complete()
        {
            local line="${COMP_LINE}"
            local point="${COMP_POINT}"
            local suggestions
            suggestions=$(groupsplit "[suggest:${point}]" "${line}" 2>/dev/null)
            COMPREPLY=($(compgen -W "${suggestions}" -- "${COMP_WORDS[COMP_CWORD]}"))
        }
        complete -F _groupsplit_complete groupsplit
        """;

    private const string Zsh = """
        # groupsplit completion. Install with:
        #   groupsplit completion zsh > "${fpath[1]}/_groupsplit"
        # or, for the current user:
        #   echo 'source <(groupsplit completion zsh)' >> ~/.zshrc
        _groupsplit_complete()
        {
            local line="${BUFFER}"
            local point="${CURSOR}"
            local -a suggestions
            suggestions=(${(f)"$(groupsplit "[suggest:${point}]" "${line}" 2>/dev/null)"})
            _describe 'groupsplit' suggestions
        }
        compdef _groupsplit_complete groupsplit
        """;

    private const string Fish = """
        # groupsplit completion. Install with:
        #   groupsplit completion fish > ~/.config/fish/completions/groupsplit.fish
        function __groupsplit_complete
            set -l line (commandline -cp)
            groupsplit "[suggest:"(string length -- $line)"]" "$line" 2>/dev/null
        end
        complete -c groupsplit -f -a '(__groupsplit_complete)'
        """;

    private const string Pwsh = """
        # groupsplit completion. Install with:
        #   groupsplit completion pwsh >> $PROFILE
        Register-ArgumentCompleter -Native -CommandName groupsplit -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            groupsplit "[suggest:$cursorPosition]" "$commandAst" 2>$null |
                ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                }
        }
        """;
}

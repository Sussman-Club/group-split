using System.CommandLine;
using System.ComponentModel;
using GroupSplit.Cli.Infrastructure;
using Spectre.Console;

namespace GroupSplit.Cli.Commands;

/// <summary>
/// Prints a shell script that wires tab completion to the <c>[suggest]</c> directive
/// <see cref="Infrastructure.Suggestions"/> answers. The scripts are thin on purpose: the
/// command tree is the single source of truth, so a new subcommand completes without
/// anyone remembering to update a static list of words here.
/// <para>
/// Each line the directive returns is a label, optionally followed by a tab and a
/// description. The shells differ only in how they carry that second half: fish reads the
/// shape natively, zsh wants a colon and its own escaping, and bash has nowhere to put it.
/// </para>
/// </summary>
public static class CompletionCommand
{
    /// <summary>
    /// Described where they are declared, because a shell shows these beside each value.
    /// Each one says where the script it prints is meant to end up.
    /// </summary>
    private enum Shell
    {
        [Description("Sourced from ~/.bashrc, or dropped in /etc/bash_completion.d.")]
        Bash,

        [Description("Written to a directory on $fpath, as _groupsplit.")]
        Zsh,

        [Description("Written to ~/.config/fish/completions/groupsplit.fish.")]
        Fish,

        [Description("Appended to $PROFILE.")]
        Pwsh
    }

    public static Command Build()
    {
        var shell = new Argument<Shell>("shell") { Description = "bash, zsh, fish or pwsh." }
            .WithDescribedValues();

        var command = new Command("completion", "Print a shell completion script.") { shell };

        command.SetHandler((context, _) =>
        {
            // Straight to stdout, unformatted: the output of this command is meant to be
            // evaluated by a shell, so a table or a JSON envelope would be unusable.
            context.RawOutput.WriteLine(context.ParseResult.GetValue(shell) switch
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
            # cut -f1: bash has no per-candidate description, so the labels go in alone.
            suggestions=$(groupsplit "[suggest:${point}]" "${line}" 2>/dev/null | cut -f1)
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
            local reply value description
            while IFS= read -r reply; do
                value="${reply%%$'\t'*}"
                description="${reply#*$'\t'}"
                [[ "${description}" == "${value}" ]] && description=""
                # _describe splits on the first colon, and a description may hold one.
                suggestions+=("${value}${description:+:${description//:/\\:}}")
            done < <(groupsplit "[suggest:${point}]" "${line}" 2>/dev/null)
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
        # Replace, never stack. A completion registered earlier for groupsplit stays live
        # in a running shell even after the file that registered it is gone, and Tab would
        # then offer the union of the two.
        complete -c groupsplit -e
        # No -d: each line already carries its own description after a tab, which is the
        # shape fish reads from a command substitution.
        complete -c groupsplit -f -a '(__groupsplit_complete)'
        """;

    private const string Pwsh = """
        # groupsplit completion. Install with:
        #   groupsplit completion pwsh >> $PROFILE
        Register-ArgumentCompleter -Native -CommandName groupsplit -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            groupsplit "[suggest:$cursorPosition]" "$commandAst" 2>$null |
                ForEach-Object {
                    $parts = $_ -split "`t", 2
                    $tooltip = if ($parts.Length -gt 1 -and $parts[1]) { $parts[1] } else { $parts[0] }
                    [System.Management.Automation.CompletionResult]::new(
                        $parts[0], $parts[0], 'ParameterValue', $tooltip)
                }
        }
        """;
}

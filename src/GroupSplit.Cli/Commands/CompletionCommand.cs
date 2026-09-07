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
/// <para>
/// A line whose label is empty is not a candidate but a hint -- what the argument at the
/// cursor is, where nothing can be completed for it. Only two shells can show one: fish
/// renders the description alone and inserts nothing on accept, and zsh has
/// <c>_message</c>. Bash and pwsh drop those lines, the latter because
/// <c>CompletionResult</c> refuses an empty label outright.
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
            # Labels alone: bash has no per-candidate description, and no way to show a
            # hint either, so a line with an empty label is dropped rather than completed.
            suggestions=$(groupsplit "[suggest:${point}]" "${line}" 2>/dev/null \
                | awk -F'\t' '$1 != "" { print $1 }')
            COMPREPLY=($(compgen -W "${suggestions}" -- "${COMP_WORDS[COMP_CWORD]}"))
        }
        complete -F _groupsplit_complete groupsplit
        """;

    private const string Zsh = """
        #compdef groupsplit
        # groupsplit completion. Install with:
        #   groupsplit completion zsh > "${fpath[1]}/_groupsplit"
        # or, for the current user:
        #   echo 'source <(groupsplit completion zsh)' >> ~/.zshrc
        #
        # The #compdef line above has to come first: it is what compinit reads to bind
        # this file to the command, and without it the fpath install completes filenames.
        _groupsplit()
        {
            local line="${BUFFER}"
            local point="${CURSOR}"
            local -a suggestions
            local reply value description hint
            while IFS= read -r reply; do
                value="${reply%%$'\t'*}"
                description="${reply#*$'\t'}"
                [[ "${description}" == "${value}" ]] && description=""
                # An empty label is a hint about what belongs here rather than something
                # to insert, and _message is how zsh shows one without offering it.
                if [[ -z "${value}" ]]; then
                    hint="${description}"
                    continue
                fi
                # _describe splits on the first colon, and a description may hold one.
                suggestions+=("${value}${description:+:${description//:/\\:}}")
            done < <(groupsplit "[suggest:${point}]" "${line}" 2>/dev/null)
            _describe 'groupsplit' suggestions
            if [[ -n "${hint}" ]]; then
                _message -r "${hint}"
                # A message shows only where a listing happens, and left to itself zsh
                # would not list: every option shares a leading dash, so it inserts that
                # much and stops. Someone who reached a positional came to type a value,
                # and half a flag is not the help they asked for.
                compstate[insert]=''
                compstate[list]='list force'
            fi
        }
        # Autoloaded out of fpath, this file is the function and has to run. Sourced from a
        # config, it only has to register -- compinit never saw it, so nothing else will.
        if [[ ${funcstack[1]} == _groupsplit ]]; then
            _groupsplit "$@"
        else
            compdef _groupsplit groupsplit
        fi
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
        # shape fish reads from a command substitution. A line whose label is empty is a
        # hint -- fish renders the description alone and accepting it inserts nothing.
        complete -c groupsplit -f -a '(__groupsplit_complete)'
        """;

    private const string Pwsh = """
        # groupsplit completion. Install with:
        #   groupsplit completion pwsh >> $PROFILE
        Register-ArgumentCompleter -Native -CommandName groupsplit -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            # An ast stringifies without the trailing space, so `groupsplit create ` arrives
            # as `groupsplit create` while the cursor still reports the space -- and every
            # position past it is read one word early. Padding back out to the cursor
            # restores what was typed, and the offset keeps that right mid-line.
            $point = $cursorPosition - $commandAst.Extent.StartOffset
            $line = "$commandAst".PadRight($point)
            groupsplit "[suggest:$point]" "$line" 2>$null |
                ForEach-Object {
                    $parts = $_ -split "`t", 2
                    # An empty label is a hint. CompletionResult rejects one, and a native
                    # completer has nowhere to show it, so it is skipped.
                    if (-not $parts[0]) { return }
                    $tooltip = if ($parts.Length -gt 1 -and $parts[1]) { $parts[1] } else { $parts[0] }
                    [System.Management.Automation.CompletionResult]::new(
                        $parts[0], $parts[0], 'ParameterValue', $tooltip)
                }
        }
        """;
}

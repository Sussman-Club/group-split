using System.Text.Json;
using GroupSplit.Cli.Infrastructure;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The parts a caller reads to learn the CLI rather than to use it: the schema an agent
/// reads instead of parsing help, and the completion scripts a shell evaluates.
/// </summary>
public sealed class SelfDescriptionTests
{
    [Fact]
    public async Task The_schema_describes_the_whole_tree()
    {
        var result = await Cli.RunAsync("schema");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var names = result.Json.GetProperty("command").GetProperty("subcommands")
            .EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();

        Assert.Contains("auth", names);
        Assert.Contains("groups", names);
        Assert.Contains("transactions", names);
        Assert.Contains("config", names);
    }

    [Fact]
    public async Task The_schema_carries_the_exit_code_table_so_it_need_not_be_guessed()
    {
        var codes = (await Cli.RunAsync("schema")).Json.GetProperty("exitCodes");

        Assert.Equal(ExitCodes.Success, codes.GetProperty("success").GetInt32());
        Assert.Equal(ExitCodes.AuthRequired, codes.GetProperty("authRequired").GetInt32());
        Assert.Equal(ExitCodes.InvalidInput, codes.GetProperty("invalidInput").GetInt32());
        Assert.Equal(ExitCodes.ConfirmationRequired, codes.GetProperty("confirmationRequired").GetInt32());
    }

    [Fact]
    public async Task The_schema_reports_arguments_with_types_a_caller_can_act_on()
    {
        var groups = (await Cli.RunAsync("schema")).Json
            .GetProperty("command").GetProperty("subcommands").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "groups");

        var show = groups.GetProperty("subcommands").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "show");

        var argument = show.GetProperty("arguments")[0];

        // "uuid", not "Guid": the reader needs to know what to type, not the CLR name.
        Assert.Equal("group-id", argument.GetProperty("name").GetString());
        Assert.Equal("uuid", argument.GetProperty("type").GetString());
        Assert.True(argument.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task The_schema_reports_the_global_options_as_recursive()
    {
        var options = (await Cli.RunAsync("schema")).Json
            .GetProperty("command").GetProperty("options").EnumerateArray().ToList();

        var json = options.Single(o => o.GetProperty("name").GetString() == "--json");
        Assert.True(json.GetProperty("recursive").GetBoolean());
        Assert.Equal("boolean", json.GetProperty("type").GetString());

        var output = options.Single(o => o.GetProperty("name").GetString() == "--output");
        Assert.Equal("auto|text|json", output.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("pwsh")]
    public async Task Completion_scripts_are_emitted_raw_for_a_shell_to_evaluate(string shell)
    {
        var result = await Cli.RunAsync("completion", shell);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        // Delegating to the parser is what keeps completions from drifting from the tree.
        Assert.Contains("[suggest:", result.Stdout);
        Assert.Contains("groupsplit", result.Stdout);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(result.Stdout));
    }

    /// <summary>
    /// What a shell asks for on Tab. The directive is answered before the parse, so these
    /// drive it exactly as the completion scripts do.
    /// </summary>
    private static async Task<List<(string Label, string Description)>> SuggestAsync(string line)
    {
        var result = await Cli.RunAsync($"[suggest:{line.Length}]", line);

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(suggestion => suggestion.Split('\t', 2))
            .Select(parts => (parts[0], parts.Length > 1 ? parts[1] : string.Empty))
            .ToList();
    }

    [Fact]
    public async Task Suggestions_at_a_command_position_are_commands_only()
    {
        var labels = (await SuggestAsync("groupsplit ")).Select(s => s.Label).ToList();

        Assert.Contains("groups", labels);
        Assert.Contains("transactions", labels);
        // The alias is a real way to type the command, so it completes like one.
        Assert.Contains("tx", labels);
        // A recursive option is valid here too, but nobody reaching for a command wants it.
        Assert.DoesNotContain(labels, label => label.StartsWith('-'));
    }

    [Fact]
    public async Task Suggestions_become_options_once_a_dash_is_typed()
    {
        var labels = (await SuggestAsync("groupsplit -")).Select(s => s.Label).ToList();

        Assert.Contains("--json", labels);
        Assert.Contains("-o", labels);
        Assert.DoesNotContain("groups", labels);
    }

    [Fact]
    public async Task Suggestions_never_offer_the_aliases_meant_for_cmd_exe()
    {
        var labels = (await SuggestAsync("groupsplit ")).Concat(await SuggestAsync("groupsplit -"))
            .Select(s => s.Label).ToList();

        Assert.DoesNotContain("/?", labels);
        Assert.DoesNotContain("/h", labels);
        Assert.DoesNotContain("-?", labels);
    }

    [Fact]
    public async Task Suggestions_carry_the_description_the_help_already_has()
    {
        var groups = Assert.Single(await SuggestAsync("groupsplit "), s => s.Label == "groups");

        Assert.Equal("Groups you belong to, their members and their balances.", groups.Description);
    }

    [Fact]
    public async Task Suggestions_reach_into_a_subcommand_and_its_argument_values()
    {
        var nested = (await SuggestAsync("groupsplit groups ")).Select(s => s.Label).ToList();
        Assert.Contains("balances", nested);
        Assert.DoesNotContain("groups", nested);

        // An enum argument is a closed set, so it completes where a free value cannot.
        var shells = (await SuggestAsync("groupsplit completion ")).Select(s => s.Label).ToList();
        Assert.Equal(["Bash", "Fish", "Pwsh", "Zsh"], shells);
    }

    [Fact]
    public async Task Suggested_values_are_described_like_everything_else()
    {
        var formats = await SuggestAsync("groupsplit -o ");

        Assert.Equal(["Auto", "Json", "Text"], formats.Select(s => s.Label));
        Assert.Equal(
            "Text when stdout is a terminal, JSON when it is not.",
            Assert.Single(formats, s => s.Label == "Auto").Description);

        // Every value carries one, and none is offered twice now that the framework's own
        // undescribed source has been replaced rather than added to.
        var shells = await SuggestAsync("groupsplit completion ");
        Assert.Equal(["Bash", "Fish", "Pwsh", "Zsh"], shells.Select(s => s.Label));
        Assert.All(shells, shell => Assert.NotEqual(string.Empty, shell.Description));
    }

    [Fact]
    public async Task A_suggest_request_without_a_position_completes_the_end_of_the_line()
    {
        var result = await Cli.RunAsync("[suggest]", "groupsplit gr");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.StartsWith("groups\t", result.Stdout);
    }

    [Fact]
    public async Task An_unknown_shell_is_a_usage_error()
    {
        var result = await Cli.RunAsync("completion", "tcsh");

        Assert.Equal(ExitCodes.InvalidInput, result.ExitCode);
    }

    [Fact]
    public async Task Help_is_written_to_the_caller_s_stdout()
    {
        var result = await Cli.RunAsync("--help");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("groups", result.Stdout);
        Assert.Contains("--json", result.Stdout);
    }
}

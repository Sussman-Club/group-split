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
        Assert.Contains("bank", names);
        Assert.Contains("inbox", names);
    }

    [Fact]
    public async Task The_schema_reports_an_enum_option_as_the_words_a_caller_would_type()
    {
        var inbox = (await Cli.RunAsync("schema")).Json
            .GetProperty("command").GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "inbox");

        var list = inbox.GetProperty("subcommands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "list");

        var status = list.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "--status");

        // The members, not "InboxStatus": an agent reading this has to know what to pass,
        // and no amount of help text substitutes for the list of accepted words.
        Assert.Equal("new|filed|ignored", status.GetProperty("type").GetString());
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

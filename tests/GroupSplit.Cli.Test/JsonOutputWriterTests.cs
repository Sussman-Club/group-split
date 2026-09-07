using System.Text.Json;
using GroupSplit.Cli.Infrastructure;
using GroupSplit.Cli.Output;
using Spectre.Console;

namespace GroupSplit.Cli.Test;

/// <summary>
/// The output contract an agent depends on: the result on stdout and nothing else, the
/// error envelope on stderr, and exit code 0 meaning stdout is safe to parse.
/// </summary>
public sealed class JsonOutputWriterTests
{
    private static (JsonOutputWriter Writer, StringWriter Out, StringWriter Error) Create(
        IReadOnlyList<string>? fields = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var settings = new OutputSettings
        {
            Format = OutputFormat.Json,
            Color = false,
            Interactive = false,
            Quiet = false,
            Fields = fields
        };

        return (new JsonOutputWriter(settings, stdout, stderr), stdout, stderr);
    }

    [Fact]
    public void Writes_the_payload_to_stdout_as_camel_case_json()
    {
        var (writer, stdout, stderr) = Create();

        writer.Write(new { GroupName = "Trip", MemberCount = 3 }, _ => new Markup("ignored"));

        var json = JsonDocument.Parse(stdout.ToString()).RootElement;

        Assert.Equal("Trip", json.GetProperty("groupName").GetString());
        Assert.Equal(3, json.GetProperty("memberCount").GetInt32());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void Errors_go_to_stderr_so_a_pipe_reading_stdout_stays_parseable()
    {
        var (writer, stdout, stderr) = Create();

        writer.WriteError(new CliError("Nope.", ErrorCodes.ServerError, "Try again."));

        Assert.Empty(stdout.ToString());

        var json = JsonDocument.Parse(stderr.ToString()).RootElement;

        Assert.Equal("Nope.", json.GetProperty("error").GetString());
        Assert.Equal("Try again.", json.GetProperty("remediation").GetString());
    }

    [Fact]
    public void Notes_and_warnings_never_touch_stdout()
    {
        var (writer, stdout, stderr) = Create();

        writer.Note("working");
        writer.Warn("careful");

        Assert.Empty(stdout.ToString());
        Assert.Contains("working", stderr.ToString());
        Assert.Contains("careful", stderr.ToString());
    }

    [Fact]
    public void Fields_narrows_an_object_to_the_requested_keys()
    {
        var (writer, stdout, _) = Create(["id"]);

        writer.Write(new { Id = 7, Name = "Trip", Secret = "x" }, _ => new Markup("ignored"));

        var json = JsonDocument.Parse(stdout.ToString()).RootElement;

        Assert.Equal(7, json.GetProperty("id").GetInt32());
        Assert.False(json.TryGetProperty("name", out _));
        Assert.False(json.TryGetProperty("secret", out _));
    }

    [Fact]
    public void Fields_narrows_every_element_of_an_array()
    {
        var (writer, stdout, _) = Create(["name"]);

        writer.Write(
            new[] { new { Id = 1, Name = "a" }, new { Id = 2, Name = "b" } },
            _ => new Markup("ignored"));

        var json = JsonDocument.Parse(stdout.ToString()).RootElement;

        Assert.Equal(2, json.GetArrayLength());
        Assert.All(
            json.EnumerateArray(),
            element =>
            {
                Assert.True(element.TryGetProperty("name", out _));
                Assert.False(element.TryGetProperty("id", out _));
            });
    }

    [Fact]
    public void Fields_matching_is_case_insensitive()
    {
        var (writer, stdout, _) = Create(["Id"]);

        writer.Write(new { Id = 7 }, _ => new Markup("ignored"));

        Assert.Equal(7, JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void Urls_and_angle_brackets_are_not_html_escaped()
    {
        // < in a remediation string is noise for every reader this has.
        var (writer, _, stderr) = Create();

        writer.WriteError(new CliError("no", "CODE", "run: groupsplit config set server <url>"));

        Assert.Contains("<url>", stderr.ToString());
        Assert.DoesNotContain("\\u003C", stderr.ToString());
    }

    [Fact]
    public void Quiet_suppresses_notes_but_never_errors()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var writer = new JsonOutputWriter(
            new OutputSettings
            {
                Format = OutputFormat.Json, Color = false, Interactive = false, Quiet = true
            },
            stdout,
            stderr);

        writer.Note("hidden");
        writer.WriteError(new CliError("shown", "CODE"));

        Assert.DoesNotContain("hidden", stderr.ToString());
        Assert.Contains("shown", stderr.ToString());
    }
}

using GroupSplit.Cli.Output;

namespace GroupSplit.Cli.Test;

[Collection(EnvironmentCollection.Name)]
public sealed class OutputSettingsTests : IDisposable
{
    private readonly EnvironmentFixture _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void An_explicit_format_is_honoured_whatever_stdout_is()
    {
        Assert.Equal(OutputFormat.Json, Resolve(OutputFormat.Json).Format);
        Assert.Equal(OutputFormat.Text, Resolve(OutputFormat.Text).Format);
    }

    [Fact]
    public void Auto_gives_json_when_stdout_is_redirected()
    {
        // The test host always runs with stdout redirected, which is exactly the shape of
        // the case that matters: something is reading the output rather than watching it.
        Assert.True(Console.IsOutputRedirected);
        Assert.Equal(OutputFormat.Json, Resolve(OutputFormat.Auto).Format);
    }

    [Fact]
    public void Colour_is_off_for_json_and_off_when_stdout_is_redirected()
    {
        Assert.False(Resolve(OutputFormat.Json).Color);
        Assert.False(Resolve(OutputFormat.Text).Color);
    }

    [Fact]
    public void No_color_environment_variable_disables_colour_whatever_its_value()
    {
        // https://no-color.org: presence is the signal, including when set to empty.
        _environment.Set("NO_COLOR", string.Empty);

        Assert.False(Resolve(OutputFormat.Text).Color);
    }

    [Fact]
    public void Quiet_and_fields_are_carried_through()
    {
        var settings = OutputSettings.Resolve(OutputFormat.Json, noColor: false, quiet: true, fields: ["id"]);

        Assert.True(settings.Quiet);
        Assert.Equal(["id"], settings.Fields);
    }

    private static OutputSettings Resolve(OutputFormat format)
        => OutputSettings.Resolve(format, noColor: false, quiet: false, fields: null);
}

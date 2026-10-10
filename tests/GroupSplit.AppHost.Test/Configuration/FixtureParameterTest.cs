using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Projects;

namespace GroupSplit.AppHost.Test.Configuration;

/// <summary>
/// The fixture's parameter values are the ones the stack runs with, whatever the machine
/// has configured (issue 331).
/// </summary>
/// <remarks>
/// Neither test ever reports a value, only a parameter's name: the value that won instead
/// of the fixture's is, by construction, somebody's real credential.
/// </remarks>
public sealed class FixtureParameterTest(AppHostFixture appHost)
{
    /// <summary>
    /// Against the stack every other test runs on. On a machine whose AppHost user secrets
    /// hold real provider settings, this is the one that fails if any of them get through.
    /// </summary>
    [Fact]
    public async Task Every_fixture_parameter_reaches_the_running_stack()
    {
        var ct = TestContext.Current.CancellationToken;

        var declared = appHost.Application.Services
            .GetRequiredService<DistributedApplicationModel>()
            .Resources
            .OfType<ParameterResource>()
            .ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);

        var overridden = new List<string>();

        foreach (var (name, expected) in AppHostFixture.Parameters)
        {
            Assert.True(declared.TryGetValue(name, out var parameter),
                $"The AppHost no longer declares '{name}'; the fixture should stop setting it.");

            if (!string.Equals(await parameter.GetValueAsync(ct), expected, StringComparison.Ordinal))
                overridden.Add(name);
        }

        Assert.Empty(overridden);
    }

    /// <summary>
    /// The same guarantee where the machine has nothing configured, which is every CI run:
    /// a competing value for every parameter, in a source ranked just below the command
    /// line -- where user secrets and environment variables sit, and as high as either can
    /// get -- still loses to the fixture's.
    /// </summary>
    /// <remarks>
    /// Builds the application model and stops there; nothing is started.
    /// </remarks>
    [Fact]
    public async Task Fixture_parameters_outrank_every_source_below_the_command_line()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<GroupSplit_AppHost>(AppHostFixture.Arguments, ct);

        // There is more than one command-line source: the host builder and the testing
        // builder each add one. The competing values go directly under the first, which is
        // as high as user secrets and environment variables ever sit, and below whichever
        // command-line source carries the fixture's arguments.
        var sources = builder.Configuration.Sources;
        var commandLine = sources.Select((source, index) => (source, index))
            .First(entry => entry.source is CommandLineConfigurationSource).index;

        sources.Insert(commandLine, new MemoryConfigurationSource
        {
            InitialData = AppHostFixture.Parameters.Keys.ToDictionary(
                name => $"Parameters:{name}",
                name => (string?)$"overridden-{name}")
        });

        var overridden = AppHostFixture.Parameters
            .Where(parameter => !string.Equals(
                builder.Configuration[$"Parameters:{parameter.Key}"], parameter.Value, StringComparison.Ordinal))
            .Select(parameter => parameter.Key)
            .ToList();

        Assert.Empty(overridden);
    }
}

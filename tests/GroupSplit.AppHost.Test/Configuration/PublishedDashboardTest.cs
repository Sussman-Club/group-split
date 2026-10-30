using Aspire.Hosting;
using GroupSplit.AppHost.Extensions;

namespace GroupSplit.AppHost.Test.Configuration;

/// <summary>
/// The deployed dashboard answers on the host's loopback address and nowhere else.
/// </summary>
/// <remarks>
/// It shows every service's environment, secrets included, over plain HTTP, so a mapping
/// Docker publishes on every interface puts both it and its token on the LAN, past the host
/// firewall. Read off the Compose file the publisher writes, because that file is the whole of
/// what the host is given: a mapping is only as narrow as the text that reaches it.
/// </remarks>
public sealed class PublishedDashboardTest : IDisposable
{
    private readonly DirectoryInfo _output = Directory.CreateTempSubdirectory("groupsplit-compose-");

    public void Dispose() => _output.Delete(recursive: true);

    [Fact]
    public async Task The_dashboard_is_published_on_loopback_only()
    {
        var builder = DistributedApplication.CreateBuilder(
        [
            "--operation", "publish",
            "--step", "publish-compose",
            "--output-path", _output.FullName
        ]);

        var token = builder.AddParameter("dashboard-token", "not-a-secret", secret: true);
        builder.AddDockerComposeEnvironment("compose").WithProtectedDashboard(token);

        await using (var app = builder.Build())
        {
            await app.RunAsync(TestContext.Current.CancellationToken);
        }

        var compose = await File.ReadAllLinesAsync(
            Path.Combine(_output.FullName, "docker-compose.yaml"),
            TestContext.Current.CancellationToken);

        var published = compose
            .SkipWhile(line => line.Trim() != "ports:")
            .Skip(1)
            .TakeWhile(line => line.TrimStart().StartsWith('-'))
            .Select(line => line.Trim().TrimStart('-').Trim().Trim('"'))
            .ToList();

        Assert.Equal([$"127.0.0.1:{DeploymentExtensions.DashboardPort}:18888"], published);

        var yaml = string.Join(Environment.NewLine, compose);
        Assert.Contains("Dashboard__TelemetryLimits__MaxLogCount: \"10000\"", yaml);
        Assert.Contains("Dashboard__TelemetryLimits__MaxTraceCount: \"10000\"", yaml);
        Assert.Contains("Dashboard__TelemetryLimits__MaxMetricsCount: \"1000\"", yaml);
    }

    [Theory]
    [InlineData("18888:18888", "127.0.0.1:18888:18888")]
    [InlineData("18888", "127.0.0.1::18888")]
    [InlineData("127.0.0.1:18888:18888", "127.0.0.1:18888:18888")]
    [InlineData("10.0.0.5:18888:18888", "10.0.0.5:18888:18888")]
    public void A_mapping_is_pinned_to_loopback_unless_it_names_an_address(string mapping, string expected)
    {
        Assert.Equal(expected, DeploymentExtensions.BindToLoopback(mapping));
    }
}

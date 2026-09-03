using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Test.Base;
using Microsoft.Extensions.DependencyInjection;
using Projects;

#pragma warning disable ASPIREDOTNETTOOL


[assembly: AssemblyFixture(typeof(AppHostFixture))]

namespace GroupSplit.AppHost.Test.Base;

public class AppHostFixture : IAsyncLifetime
{
    /// <summary>
    /// How long the whole stack gets to come up. Generous, because a cold run pulls
    /// container images on a cold CI runner and imports the Keycloak realm, but bounded:
    /// everything here waits on a resource reaching a state, and an unbounded wait for
    /// a state that never arrives is indistinguishable from a hang.
    /// </summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(8);

    /// <summary>
    /// How long any one <c>WaitFor…</c> in a test gets. Shorter than the startup
    /// budget, because by the time a test runs the stack is already up.
    /// </summary>
    public static readonly TimeSpan ResourceTimeout = TimeSpan.FromMinutes(3);

    public DistributedApplication Application { get; private set; } = null!;

    private readonly List<string> _log = [];
    private CancellationTokenSource? _watch;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<GroupSplit_AppHost>();

        builder.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        Application = await builder.BuildAsync();

        // Started before StartAsync so that a stall inside start-up is recorded too.
        StartRecordingResourceStates();

        using var startup = new CancellationTokenSource(StartupTimeout);

        try
        {
            await Application.StartAsync(startup.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"The AppHost did not finish starting within {StartupTimeout.TotalMinutes:0} minutes."
                + Environment.NewLine + DescribeResources());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watch is not null)
        {
            await _watch.CancelAsync();
            _watch.Dispose();
        }

        await Application.StopAsync();
        await Application.DisposeAsync();
    }

    /// <summary>
    /// Waits for a resource, and says which resources were in which state if it never
    /// gets there. Without the trailing report a timeout names only the resource that
    /// was waited on, which is rarely the one that actually failed to start.
    /// </summary>
    public async Task WaitForAsync(
        string resourceName,
        Func<ResourceNotificationService, CancellationToken, Task> wait,
        string what)
    {
        using var timeout = new CancellationTokenSource(ResourceTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);

        try
        {
            await wait(Application.ResourceNotifications, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Resource '{resourceName}' did not {what} within "
                + $"{ResourceTimeout.TotalMinutes:0} minutes."
                + Environment.NewLine + DescribeResources());
        }
    }

    /// <summary>The last state seen for every resource, newest first.</summary>
    public string DescribeResources()
    {
        lock (_log)
        {
            return _log.Count == 0
                ? "No resource state was reported."
                : "Last reported resource states:" + Environment.NewLine
                    + string.Join(Environment.NewLine, _log);
        }
    }

    private void StartRecordingResourceStates()
    {
        _watch = new CancellationTokenSource();
        var token = _watch.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in Application.ResourceNotifications.WatchAsync(token))
                {
                    var line = $"  {change.Resource.Name,-24} "
                        + $"{change.Snapshot.State?.Text ?? "unknown",-12} "
                        + $"health: {change.Snapshot.HealthStatus?.ToString() ?? "none"}";

                    lock (_log)
                    {
                        _log.RemoveAll(entry => entry.StartsWith($"  {change.Resource.Name,-24} ",
                            StringComparison.Ordinal));
                        _log.Add(line);
                        _log.Sort(StringComparer.Ordinal);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }, token);
    }
}

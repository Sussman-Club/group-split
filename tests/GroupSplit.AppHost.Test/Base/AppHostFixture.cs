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

    /// <summary>Last state text per resource, so a failed one can be found by name.</summary>
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);

    private CancellationTokenSource? _watch;

    /// <summary>
    /// States a resource lands in when it is not coming back. A resource in one of these
    /// is the thing to look at; the resource a test was waiting on is usually just
    /// downstream of it.
    /// </summary>
    private static readonly string[] FailedStates =
        [KnownResourceStates.FailedToStart, KnownResourceStates.Exited];

    /// <summary>How many trailing log lines to quote from a resource that failed.</summary>
    private const int LogTailLines = 40;

    /// <summary>
    /// How long to spend collecting those lines. The logs are already buffered, so this is
    /// only here because the enumerator keeps watching rather than completing on its own.
    /// </summary>
    private static readonly TimeSpan LogCollectionTimeout = TimeSpan.FromSeconds(10);

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
                + Environment.NewLine + await DescribeFailuresAsync());
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
                + Environment.NewLine + await DescribeFailuresAsync());
        }
        // A resource that fails outright throws instead of timing out, and Aspire's
        // message names only the resource that was waited on -- 'web' here -- which is
        // usually downstream of the one that actually broke. Without this the failure
        // arrives as a bare stack trace: no states, no indication of which resource to
        // look at, and nothing to tell a genuine break from a second AppHost running
        // beside this one and taking the ports and container names.
        catch (DistributedApplicationException exception)
        {
            throw new DistributedApplicationException(
                $"Resource '{resourceName}' did not {what}: {exception.Message}"
                + Environment.NewLine + await DescribeFailuresAsync(),
                exception);
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

    /// <summary>
    /// The resource states, plus the tail of the console log of every resource that failed.
    /// The states alone say which resource broke but never why, and the why is in the
    /// resource's own output -- which otherwise only exists in the test host's console,
    /// where a CI run or an IDE test pane may not surface it beside the failure.
    /// </summary>
    public async Task<string> DescribeFailuresAsync()
    {
        string[] failed;

        lock (_log)
        {
            failed = _states
                .Where(entry => FailedStates.Contains(entry.Value, StringComparer.Ordinal))
                .Select(entry => entry.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        var report = DescribeResources();

        foreach (var name in failed)
        {
            report += Environment.NewLine + Environment.NewLine
                + $"Last {LogTailLines} log lines from '{name}':" + Environment.NewLine
                + await ReadLogTailAsync(name);
        }

        return report;
    }

    private async Task<string> ReadLogTailAsync(string resourceName)
    {
        var logs = Application.Services.GetRequiredService<ResourceLoggerService>();
        var tail = new Queue<string>(LogTailLines);

        using var timeout = new CancellationTokenSource(LogCollectionTimeout);

        try
        {
            // GetAllAsync replays what was buffered and then keeps watching, so it never
            // completes on its own -- the timeout above is what ends the enumeration.
            await foreach (var batch in logs.GetAllAsync(resourceName)
                               .WithCancellation(timeout.Token))
            {
                foreach (var line in batch)
                {
                    if (tail.Count == LogTailLines)
                        tail.Dequeue();

                    tail.Enqueue($"    {line.Content}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the enumeration is ended by the timeout, not by the source.
        }

        return tail.Count == 0
            ? "    (no output was captured)"
            : string.Join(Environment.NewLine, tail);
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
                        _states[change.Resource.Name] = change.Snapshot.State?.Text ?? "unknown";
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

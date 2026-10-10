using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using GroupSplit.AppHost.Extensions;
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

    /// <summary>
    /// Set to <c>1</c> or <c>true</c> to run the Aspire dashboard beside the stack under test,
    /// so a test stopped at a breakpoint, or a stack that will not come up, can be looked at
    /// the way a normal run can. The sign-in URL is written to standard output once the
    /// application has started.
    /// </summary>
    /// <remarks>
    /// Off by default, and never on in CI: nobody is there to open it, and the URL carries
    /// the browser token. It changes nothing else about the run -- the dashboard binds to
    /// loopback ports of its own choosing, and waits keep stopping on a failed resource
    /// exactly as they do without it.
    /// </remarks>
    public const string DashboardVariable = "GROUPSPLIT_TEST_DASHBOARD";

    private static bool DashboardEnabled =>
        Environment.GetEnvironmentVariable(DashboardVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    public DistributedApplication Application { get; private set; } = null!;

    /// <summary>Sets the realm up for the tests that need an account or a short-lived token.</summary>
    public KeycloakAdmin Keycloak { get; private set; } = null!;

    private readonly List<string> _log = [];

    /// <summary>Last state text per resource, so a failed one can be found by name.</summary>
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);

    private CancellationTokenSource? _watch;

    /// <summary>The admin the AppHost was given, which is not always the default above.</summary>
    private string _adminUsername = KeycloakAdmin.DefaultUsername;

    private string _adminPassword = KeycloakAdmin.DefaultPassword;

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
        // No dev tunnel for the tests. It is a real tunnel to the public internet: it needs
        // the devtunnel CLI and a signed-in account, which a CI runner has neither of, and
        // nothing here calls in from outside.
        //
        // Left to itself the failure is total rather than local: the API takes its webhook
        // origin from the tunnel's endpoint, so a tunnel that cannot start leaves the API
        // waiting on it, the web app waiting on the API, and the whole stack short of the
        // start-up budget.
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<GroupSplit_AppHost>(
                new DistributedApplicationTestingBuilderOptions { EnableDashboard = DashboardEnabled },
                [
                    $"--{WebhookTunnelExtensions.EnabledKey}=false"
                ]);

        builder.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        // Aspire generates the Keycloak admin password and gives a test no way to read it.
        // Supplied, so these tests can create an account and shorten the realm's token.
        // Aspire generates the Keycloak admin password and gives a test no way to read it,
        // so these fill it in -- but only when nothing else has.
        //
        // Overwriting was the bug. Keycloak creates its admin exactly once, into an empty
        // database, and this stack's Keycloak lives in a Postgres database on a data
        // volume that outlives `aspire stop`. On a machine that has run the AppHost
        // normally, that admin already exists with the developer's own password from user
        // secrets; handing Keycloak a different one here changes nothing at the container
        // and leaves this fixture signing in with a password no admin has. Every test in
        // the assembly then failed with `invalid_grant`, on any machine that had ever run
        // the app -- and passed in CI, where the volume does not exist yet.
        builder.Configuration["Parameters:keycloak-username"] ??= KeycloakAdmin.DefaultUsername;
        builder.Configuration["Parameters:keycloak-password"] ??= KeycloakAdmin.DefaultPassword;

        // The AppHost deliberately has no in-code parameter defaults. The integration
        // tests exercise the disabled-provider paths, so give every shared parameter an
        // explicit harmless value before building the application model.
        var testParameters = new Dictionary<string, string?>
        {
            ["Parameters:receipt-transcription-provider"] = "Veryfi",
            ["Parameters:azure-openai-enabled"] = "false",
            ["Parameters:azure-openai-endpoint"] = "https://azure-openai.test/openai/v1/",
            ["Parameters:azure-openai-model"] = "test-receipt-model",
            ["Parameters:azure-openai-api-key"] = "test-azure-openai-key",
            ["Parameters:veryfi-enabled"] = "false",
            ["Parameters:veryfi-client-id"] = "test-veryfi-client-id",
            ["Parameters:veryfi-username"] = "test-veryfi-username",
            ["Parameters:veryfi-api-key"] = "test-veryfi-api-key",
            ["Parameters:veryfi-log-raw-responses"] = "false",
            ["Parameters:google-sign-in-enabled"] = "false",
            ["Parameters:google-client-id"] = "test-google-client-id",
            ["Parameters:google-client-secret"] = "test-google-client-secret",
            ["Parameters:plaid-enabled"] = "false",
            ["Parameters:plaid-client-id"] = "test-plaid-client-id",
            ["Parameters:plaid-secret"] = "test-plaid-secret",
            ["Parameters:plaid-env"] = "Sandbox",
        };

        foreach (var (key, value) in testParameters)
            builder.Configuration[key] ??= value;

        _adminUsername = builder.Configuration["Parameters:keycloak-username"]!;
        _adminPassword = builder.Configuration["Parameters:keycloak-password"]!;

        // The render mode a deployment runs. appsettings.Development.json says
        // WebAssemblyFirst, so without this the browser tests exercise the one path
        // production does not take -- and a token frozen inside an interactive circuit is
        // invisible from the other one.
        builder
            .CreateResourceBuilder(
                builder.Resources.OfType<IResourceWithEnvironment>().Single(resource => resource.Name == "web"))
            .WithEnvironment("RenderMode", "ServerFirst");

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

        if (DashboardEnabled)
        {
            using var dashboard = new CancellationTokenSource(ResourceTimeout);
            Console.WriteLine($"Aspire dashboard: {await Application.GetDashboardUrlAsync(dashboard.Token)}");
        }

        await ConfigureRealmAsync();
    }

    /// <summary>
    /// Waits for Keycloak and shortens its access token, once for the whole run rather than
    /// per test: the realm is shared, so a test that changed it and put it back would still
    /// be visible to anything running beside it.
    /// </summary>
    private async Task ConfigureRealmAsync()
    {
        await WaitForAsync(
            "keycloak",
            (notifications, token) => notifications.WaitForResourceHealthyAsync("keycloak", token),
            "become healthy");

        // Named endpoint, not the default: Keycloak publishes a management port beside its
        // http one, and the relative admin paths below need the http one specifically. The
        // trailing slash matters too -- without it Uri resolution drops the last segment.
        var keycloak = new Uri(Application.GetEndpoint("keycloak", "http").AbsoluteUri.TrimEnd('/') + "/");

        // The credentials the AppHost was actually given, which are the developer's own
        // where they had any.
        Keycloak = new KeycloakAdmin(
            new HttpClient { BaseAddress = keycloak },
            _adminUsername,
            _adminPassword);

        using var timeout = new CancellationTokenSource(ResourceTimeout);

        await Keycloak.ShortenAccessTokenLifespanAsync(timeout.Token);
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

    /// <summary>
    /// Starts the <c>realm-settings</c> worker again and waits for that run, not the one at
    /// start-up, to finish successfully. The tests that call this take the realm apart first,
    /// so they share <c>GoogleFirstBrokerLogin.Collection</c> and run one at a time.
    /// </summary>
    public async Task RunRealmWorkerAsync()
    {
        const string worker = "realm-settings";

        var notifications = Application.ResourceNotifications;

        // Subscribed before the start, so a run quick enough to finish before the wait began
        // cannot be mistaken for the one before it.
        var started = notifications.WaitForResourceAsync(
            worker, resource => !IsDone(resource.Snapshot), TestContext.Current.CancellationToken);

        var start = await Application.ResourceCommands.ExecuteCommandAsync(
            worker, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);

        Assert.True(start.Success, $"Starting {worker} failed: {start.Message ?? "no error message"}.");

        await started;

        ResourceEvent? finished = null;

        await WaitForAsync(
            worker,
            async (resources, token) => finished = await resources.WaitForResourceAsync(
                worker, resource => IsDone(resource.Snapshot), token),
            "finish its run");

        Assert.True(
            finished!.Snapshot.ExitCode == 0,
            $"{worker} exited with {finished.Snapshot.ExitCode}:" + Environment.NewLine
            + await ReadLogAsync(worker));

        // The worker puts the realm's token lifespan back to the file's, and the tests that
        // wait out an expiry rely on the short one set at start-up.
        await Keycloak.ShortenAccessTokenLifespanAsync(TestContext.Current.CancellationToken);

        static bool IsDone(CustomResourceSnapshot snapshot) =>
            snapshot.State?.Text is { } state
            && (state == KnownResourceStates.Finished || state == KnownResourceStates.Exited
                || state == KnownResourceStates.FailedToStart);
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

    /// <summary>
    /// What a resource has logged so far. Public because some things are only observable
    /// here: the app is built to recover from a 401 by signing in again, so from a browser
    /// a token that refreshed cleanly and one that failed and recovered look the same.
    /// </summary>
    public Task<string> ReadLogAsync(string resourceName) =>
        ReadLogTailAsync(resourceName, maxLines: 2000);

    private async Task<string> ReadLogTailAsync(string resourceName, int? maxLines = null)
    {
        var lineBudget = maxLines ?? LogTailLines;

        var logs = Application.Services.GetRequiredService<ResourceLoggerService>();
        var tail = new Queue<string>(lineBudget);

        // A container's log is kept under its instance name (keycloak-abcdefgh), not the
        // name in the app model, so asking by name alone finds nothing for one. The resource
        // overload resolves the instance names; a name the model does not have is asked for
        // as it is.
        var resource = Application.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.FirstOrDefault(candidate => candidate.Name == resourceName);
        var source = resource is null ? logs.GetAllAsync(resourceName) : logs.GetAllAsync(resource);

        using var timeout = new CancellationTokenSource(LogCollectionTimeout);

        try
        {
            // GetAllAsync replays what was buffered and then keeps watching, so it never
            // completes on its own -- the timeout above is what ends the enumeration.
            await foreach (var batch in source.WithCancellation(timeout.Token))
            {
                foreach (var line in batch)
                {
                    if (tail.Count == lineBudget)
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

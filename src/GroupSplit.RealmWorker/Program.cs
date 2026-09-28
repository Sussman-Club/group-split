using GroupSplit.Keycloak;
using GroupSplit.RealmWorker;

var builder = Host.CreateApplicationBuilder(args);
// Logs to the dashboard as well as stdout. Deployed, this worker's stdout is only readable on
// the host, and a failure here is what keeps the API from starting -- the reason has to be
// somewhere an operator can see it without a shell there. OpenTelemetry alone rather than the
// full service defaults, whose resilience handler would retry the admin calls behind the
// reconciler's back.
builder.ConfigureOpenTelemetry();
builder.AddKeycloakAdminClient();

using var host = builder.Build();
await host.StartAsync();

var keycloak = host.Services.GetRequiredService<IKeycloakRealmAdminClient>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RealmWorker");
var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

// A failure is logged and turned into exit code 1, rather than left to escape as an unhandled
// exception. The API waits for this worker to complete successfully, so a non-zero exit is
// what keeps it from starting against a realm that was not reconciled, and the log is the
// only place that says why.
try
{
    switch (builder.Configuration["Realm:Operation"])
    {
        case "reconcile":
            await new RealmSettingsReconciler(keycloak, logger, name => builder.Configuration[name])
                .ReconcileAsync(stopping);
            break;

        case "reset":
            await keycloak.DeleteRealmAsync(stopping);
            logger.LogWarning(
                "Deleted Keycloak realm {Realm}. Restart Keycloak to import its repository definition.",
                keycloak.Realm);
            break;

        default:
            throw new InvalidOperationException("Realm:Operation must be 'reconcile' or 'reset'.");
    }
}
catch (Exception exception) when (exception is not OperationCanceledException || !stopping.IsCancellationRequested)
{
    logger.LogCritical(exception, "The realm worker failed: {Reason}", exception.Message);
    Environment.ExitCode = 1;
}

await host.StopAsync();

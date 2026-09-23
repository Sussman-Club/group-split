using GroupSplit.Keycloak;
using GroupSplit.RealmWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKeycloakAdminClient();

using var host = builder.Build();
await host.StartAsync();

var keycloak = host.Services.GetRequiredService<IKeycloakRealmAdminClient>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RealmWorker");

switch (builder.Configuration["Realm:Operation"])
{
    case "reconcile":
        await new RealmSettingsReconciler(keycloak, logger)
            .ReconcileAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        break;

    case "reset":
        await keycloak.DeleteRealmAsync(
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        logger.LogWarning(
            "Deleted Keycloak realm {Realm}. Restart Keycloak to import its repository definition.",
            keycloak.Realm);
        break;

    default:
        throw new InvalidOperationException("Realm:Operation must be 'reconcile' or 'reset'.");
}

await host.StopAsync();

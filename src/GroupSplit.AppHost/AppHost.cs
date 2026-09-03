using GroupSplit.AppHost.Deployment;
using GroupSplit.AppHost.Keycloak;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001

var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard => dashboard.WithHostPort(18888));

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithPgWeb();

var db = dbServer.AddDatabase("db", "groupsplit");

var keycloakDb = dbServer.AddDatabase("keycloak-db", "keycloak");

var keycloak = builder.AddKeycloak("keycloak")
    .WithGoogleSignIn(builder.Configuration)
    .WithPostgres(keycloakDb)
    .WaitFor(keycloakDb)
    .WithOtlpExporter();

var migrations = db
    .AddEFMigrations("migrations",
        "../GroupSplit.Data.PostgreSQL.Migrations/GroupSplit.Data.PostgreSQL.Migrations.csproj",
        dbContextTypeName: "AppDbContext",
        connectionName: "DefaultConnection",
        configureProjectResource: project => project.ExcludeFromManifest())
    .RunDatabaseUpdateOnStart()
    .PublishAsMigrationBundle(publishContainer: true)
    .PublishAsDockerComposeService((_, service) => service.Restart = "no");

var backend = builder.AddProject<GroupSplit_API>("api")
    .WaitFor(db)
    .WithReference(db)
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WaitForCompletion(migrations)
    .WithHttpProbe(ProbeType.Liveness, "/alive")
    .WithHttpProbe(ProbeType.Readiness, "/health");

var frontend = builder.AddProject<GroupSplit_App_Web>("web")
    .WithReference(keycloak)
    .WaitFor(backend)
    .WithReference(backend)
    .WithHttpProbe(ProbeType.Liveness, "/alive")
    .WithHttpProbe(ProbeType.Readiness, "/health")
    .WithBrowserLogs();

if (builder.ExecutionContext.IsRunMode)
{
    db.WithPostgresMcp();
    keycloakDb.WithPostgresMcp();

    // Mounted from disk so realm and theme edits only need a restart, not a rebuild.
    keycloak
        .WithRealmImport("./realms.json")
        .WithBindMount("./keycloak-themes/group-split", "/opt/keycloak/themes/group-split", isReadOnly: true)
        .WithDataVolume();

    builder
        .AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj")
        .AddWindowsDevice()
        .WithReference(backend)
        .WaitFor(backend);

    builder
        .AddSeeder<GroupSplit_Seeder>("seeder")
        .WaitFor(db)
        .WithReference(db)
        .WithResetAndSeedCommand()
        .WaitForCompletion(migrations);

    builder.AddScalarApiReference().WithApiReference(backend);
}
else
{
    // Has to resolve to the same address from a browser and from inside the Compose network.
    // "localhost" cannot: a container resolves it to itself.
    var keycloakHostname = builder.AddParameterFromConfiguration("keycloak-hostname", "Keycloak:Hostname");

    var authority = ReferenceExpression.Create($"{keycloakHostname}/realms/group-split");

    keycloak
        .AsDeployedKeycloak(keycloakHostname)
        .PublishOnHostPort(8081);

    frontend
        .WithKeycloakAuthority(authority)
        .PublishOnHostPort(8080);

    // The API stays internal: the web app forwards to it and the WASM client uses its own origin.
    // It still validates browser-issued tokens, so it needs the same public issuer.
    backend.WithKeycloakAuthority(authority);

    dbServer.WithBakedInitScript("postgres-init/create-databases.sql");

    compose.WithComposeDefaults(databaseService: dbServer.Resource.Name);
}

var host = builder.Build();

host.Run();
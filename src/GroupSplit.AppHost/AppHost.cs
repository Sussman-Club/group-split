using GroupSplit.AppHost.Keycloak;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001

var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("env").WithDashboard();

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithPgWeb()
    .WithTerminal();

var db = dbServer.AddDatabase("db", "groupsplit");

var keycloakDb = dbServer.AddDatabase("keycloak-db", "keycloak");

var keycloak = builder.AddKeycloak("keycloak")
    .WithGoogleSignIn(builder.Configuration)
    .WithSmtp(builder.Configuration)
    .WithRealmImport("./realms.json")
    .WithBindMount("./keycloak-themes/group-split", "/opt/keycloak/themes/group-split", isReadOnly: true)
    .WithDataVolume()
    .WithPostgres(keycloakDb)
    .WaitFor(keycloakDb)
    .WithOtlpExporter()
    .WithTerminal()
    .WithExternalHttpEndpoints();

// Developer tooling: an unrestricted SQL endpoint and a mail catcher have no place in a deployment.
if (builder.ExecutionContext.IsRunMode)
{
    db.WithPostgresMcp();
    keycloakDb.WithPostgresMcp();

    var mailpit = builder.AddMailPit("mailpit");

    keycloak.WaitFor(mailpit);
}

var backend = builder.AddProject<GroupSplit_API>("api")
    .WaitFor(db)
    .WithReference(db)
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithHttpProbe(ProbeType.Liveness, "/alive")
    .WithHttpProbe(ProbeType.Readiness, "/health");

var frontend = builder.AddProject<GroupSplit_App_Web>("web")
    .WithReference(keycloak)
    .WaitFor(backend)
    .WithReference(backend)
    .WithHttpProbe(ProbeType.Liveness, "/alive")
    .WithHttpProbe(ProbeType.Readiness, "/health")
    .WithExternalHttpEndpoints()
    .WithBrowserLogs();

// The MAUI client is a locally launched device app, not a deployable container workload.
if (builder.ExecutionContext.IsRunMode)
{
    var mauiapp = builder.AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj");

    mauiapp.AddWindowsDevice().WithReference(backend).WaitFor(backend);
}

const string migrationsName = "migrations";

var migrations = db
    .AddEFMigrations(migrationsName,
        "../GroupSplit.Data.PostgreSQL.Migrations/GroupSplit.Data.PostgreSQL.Migrations.csproj",
        dbContextTypeName: "AppDbContext",
        connectionName: "DefaultConnection")
    .RunDatabaseUpdateOnStart()
    // Deployments get the migrations as a run-once container so the schema exists before anything uses it.
    .PublishAsMigrationBundle(publishContainer: true)
    .PublishAsDockerComposeService((_, service) => service.Restart = "no");

// Nothing should touch the schema until migrations have been applied.
backend.WaitForCompletion(migrations);

// The seeder is a local development convenience, not a deployed workload.
if (builder.ExecutionContext.IsRunMode)
{
    builder
        .AddSeeder<GroupSplit_Seeder>("seeder")
        .WaitFor(db)
        .WithReference(db)
        .WithResetAndSeedCommand()
        .WaitForCompletion(migrations);
}

compose.ConfigureComposeFile(file =>
{
    // AddEFMigrations also emits its wrapper resource as an empty compute service with no image
    // behind it. Only the generated bundle container above does the real work.
    file.Services.Remove(migrationsName);
});

if (builder.ExecutionContext.IsRunMode)
{
    builder.AddScalarApiReference().WithApiReference(backend);
}

var host = builder.Build();

host.Run();
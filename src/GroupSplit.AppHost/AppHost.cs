using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using GroupSplit.AppHost.Keycloak;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIREDOCKERFILEBUILDER001, ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001

var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard => dashboard.WithHostPort(18888));

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithPgWeb()
    .WithTerminal();

var db = dbServer.AddDatabase("db", "groupsplit");

var keycloakDb = dbServer.AddDatabase("keycloak-db", "keycloak");

var keycloak = builder.AddKeycloak("keycloak")
    .WithGoogleSignIn(builder.Configuration)
    .WithPostgres(keycloakDb)
    .WaitFor(keycloakDb)
    .WithOtlpExporter()
    .WithTerminal()
    .WithExternalHttpEndpoints();

ReferenceExpression? keycloakAuthority = null;
var requireHttpsMetadata = true;

// Developer tooling: an unrestricted SQL endpoint and a mail catcher have no place in a deployment.
if (builder.ExecutionContext.IsRunMode)
{
    db.WithPostgresMcp();
    keycloakDb.WithPostgresMcp();

    var mailpit = builder.AddMailPit("mailpit");

    keycloak.WaitFor(mailpit);

    // Mounted from disk so realm and theme edits only need a restart, not a rebuild.
    keycloak
        .WithRealmImport("./realms.json")
        .WithBindMount("./keycloak-themes/group-split", "/opt/keycloak/themes/group-split", isReadOnly: true)
        .WithDataVolume();
}
else
{
    // The browser and the back channel reach Keycloak by different names, so the issuer has to be
    // pinned to the address users actually hit rather than the compose service name.
    var keycloakHostname = builder.AddParameter(
        "keycloak-hostname",
        () => builder.Configuration["Keycloak:Hostname"] ?? string.Empty);

    keycloak.AsDeployedKeycloak(keycloakHostname);

    // Applied once the web resource exists, further down.
    keycloakAuthority = ReferenceExpression.Create($"{keycloakHostname}/realms/group-split");

    // This deployment terminates no TLS, so the OIDC authority is plain http and metadata
    // validation has to be told to accept it. Revisit the moment HTTPS is in front of Keycloak.
    requireHttpsMetadata = false;

    // Aspire creates AddDatabase() databases through the orchestrator, which does not run in a
    // deployment, so bake an init script the Postgres entrypoint picks up on first start.
    var postgresImage = dbServer.Resource.Annotations.OfType<ContainerImageAnnotation>().Last();

    dbServer.WithDockerfileBuilder(".", context =>
    {
        context.Builder
            .From(postgresImage.Registry is null
                ? $"{postgresImage.Image}:{postgresImage.Tag}"
                : $"{postgresImage.Registry}/{postgresImage.Image}:{postgresImage.Tag}")
            .Copy("postgres-init/create-databases.sql", "/docker-entrypoint-initdb.d/10-create-databases.sql");
    });
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

// Outside development the web app refuses to start without an explicit authority, because service
// discovery hands it an https+http:// address that metadata validation rejects.
if (keycloakAuthority is not null)
{
    frontend
        .WithEnvironment("Keycloak__Authority", keycloakAuthority)
        .WithEnvironment("Keycloak__RequireHttpsMetadata", requireHttpsMetadata ? "true" : "false");
}

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
        connectionName: "DefaultConnection",
        // The startup project exists only to give EF a project to run against and is never
        // launched, but publish still tries to build and tag a container image it never produced.
        configureProjectResource: project => project.ExcludeFromManifest())
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
    // Compose defaults to no restart policy, so a crash or a host reboot leaves the
    // stack down. The one-shot migration bundle keeps its own "no".
    foreach (var service in file.Services.Values)
    {
        service.Restart ??= "unless-stopped";
    }

    // depends_on defaults to service_started, so consumers race Postgres while it is still
    // running its first-time init. Gate them on a real readiness probe instead.
    var database = file.Services["db-server"];

    database.Healthcheck = new Healthcheck
    {
        Test = ["CMD-SHELL", "pg_isready -U postgres -d postgres"],
        Interval = "5s",
        Timeout = "5s",
        Retries = 12,
        StartPeriod = "10s"
    };

    foreach (var service in file.Services.Values)
    {
        if (service.DependsOn.TryGetValue("db-server", out var onDatabase))
        {
            onDatabase.Condition = "service_healthy";
        }
    }

    // Published ports otherwise land on a random host port, which makes the
    // deployment unreachable without inspecting docker ps first.
    PublishOnHostPort(file.Services["web"], 8080);
    PublishOnHostPort(file.Services["keycloak"], 8081);

    // Keeps the container port Aspire chose, but pins the host side. Any extra
    // ports are dropped: for Keycloak that is 9000, the management endpoint.
    static void PublishOnHostPort(Service service, int hostPort)
    {
        if (service.Ports is [var containerPort, ..])
        {
            service.Ports = [$"{hostPort}:{containerPort}"];
        }
    }
});

if (builder.ExecutionContext.IsRunMode)
{
    builder.AddScalarApiReference().WithApiReference(backend);
}

var host = builder.Build();

host.Run();
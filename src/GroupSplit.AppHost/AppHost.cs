using Aspire.Hosting.Docker.Resources.ServiceNodes;
using GroupSplit.AppHost.Deployment;
using GroupSplit.AppHost.Keycloak;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIRECOMPUTE003, ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001, ASPIREPIPELINES001

var builder = DistributedApplication.CreateBuilder(args);

// Left unset, the dashboard mints a fresh login token on every container start and writes it
// only to its own logs, so the URL from the last deploy stops working and the token has to be
// dug back out of `docker logs`. Setting one keeps a single stable login URL. Optional rather
// than required: a required parameter fails the whole deploy whenever the secret is absent,
// which is not a trade worth making for a convenience. The deploy workflow already exports a
// DASHBOARD_TOKEN secret into this key along with every other one, so nothing there changes.
var dashboardToken = builder.Configuration["Parameters:dashboard-token"];

var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard =>
    {
        dashboard.WithHostPort(18888)
                 // Behind TLS terminated upstream, the dashboard has to learn the original
                 // scheme and host the way Keycloak does, or its redirects point back at
                 // plain HTTP.
                 .WithForwardedHeaders(enabled: true);

        if (string.IsNullOrWhiteSpace(dashboardToken))
        {
            return;
        }

        // A parameter rather than the raw string, so the value is masked in the dashboard and
        // travels as an env placeholder instead of being inlined into the Compose file.
        var token = builder.AddParameter(
            "dashboard-token", () => dashboardToken, secret: true);

        dashboard.WithEnvironment("Dashboard__Frontend__AuthMode", "BrowserToken")
                 .WithEnvironment("Dashboard__Frontend__BrowserToken", token);
    });

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

    keycloak
        .WithRealmImport("./realms.json")
        .WithContainerFiles("/opt/keycloak/themes/group-split", "./keycloak-themes/group-split")
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
    var keycloakHostname = builder.AddParameter("keycloak-hostname");

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

    // A remote host can only pull images it can reach, so publish tags into the shared
    // registry rather than leaving them tagged on whatever machine ran the deploy.
    var registry = builder.AddContainerRegistry("registry", "registry.sussman.win", "group-split");

    compose.WithContainerRegistry(registry);

    // Shipped as Compose configs rather than baked into derived images: the base layers
    // would then have to travel through the registry on every deploy.
    dbServer
        .WithFiles(
            "postgres-init/create-databases.sql",
            "/docker-entrypoint-initdb.d/10-create-databases.sql")
        .WithComposeHealthcheck(new Healthcheck
        {
            Test = ["CMD-SHELL", "pg_isready -U postgres -d postgres"],
            Interval = "5s",
            Timeout = "5s",
            Retries = 12,
            StartPeriod = "10s"
        });

    keycloak
        .WithFiles("realms.json", "/opt/keycloak/data/import/realms.json")
        .WithFiles("keycloak-themes/group-split", "/opt/keycloak/themes/group-split");

    // Gives web's WaitFor(api) something to wait on: WithComposeDefaults upgrades that edge
    // once this service reports healthy. Same probe shape as Keycloak, for the same reason --
    // no CLI HTTP client in the image -- against the readiness endpoint the API maps outside
    // development. Explicitly bash, not CMD-SHELL: /bin/sh here is dash, which has no
    // /dev/tcp. The "$$" is how a Compose file escapes a literal "$", leaving HTTP_PORTS for
    // the container's own shell rather than Compose's interpolation.
    backend.WithComposeHealthcheck(new Healthcheck
    {
        Test =
        [
            "CMD", "bash", "-c",
            @"{ printf 'HEAD /health HTTP/1.0\r\n\r\n' >&0; grep -q '^HTTP/1\.[01] 200'; } 0<>/dev/tcp/localhost/$$HTTP_PORTS"
        ],
        Interval = "5s",
        Timeout = "5s",
        Retries = 12,
        StartPeriod = "30s"
    });

    compose.WithComposeDefaults();

    // A barrier so the pushes and the Compose generation share one pipeline execution.
    // Run as separate `aspire do` invocations they each get their own deploy-prereq,
    // and that stamps a fresh timestamp tag every time: the generated compose file then
    // points at a tag nothing was ever pushed under, and the pull fails with
    // "manifest unknown".
    builder.Pipeline.AddStep(
        "push-and-prepare-compose",
        _ => Task.CompletedTask,
        dependsOn: new[]
        {
            "prepare-compose",
            $"push-{backend.Resource.Name}",
            $"push-{frontend.Resource.Name}",
            $"push-{migrations.Resource.Name}",
        });
}

var host = builder.Build();

host.Run();

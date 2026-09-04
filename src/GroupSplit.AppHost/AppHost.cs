using Aspire.Hosting.Docker.Resources.ServiceNodes;
using GroupSplit.AppHost.Deployment;
using GroupSplit.AppHost.Health;
using GroupSplit.AppHost.Keycloak;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIRECOMPUTE003, ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001, ASPIREPIPELINES001

var builder = DistributedApplication.CreateBuilder(args);

var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard =>
    {
        dashboard.WithHostPort(18888)
            // Behind TLS terminated upstream, the dashboard has to learn the original
            // scheme and host the way Keycloak does, or its redirects point back at
            // plain HTTP.
            .WithForwardedHeaders(enabled: true);

        var dashboardToken = builder.Configuration["Parameters:dashboard-token"];

        if (string.IsNullOrWhiteSpace(dashboardToken))
            return;

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
    .WithHealthEndpoints();

var frontend = builder.AddProject<GroupSplit_App_Web>("web")
    .WithReference(keycloak)
    .WaitFor(backend)
    .WithReference(backend)
    .WithHealthEndpoints()
    .WithBrowserLogs();

if (builder.ExecutionContext.IsRunMode)
{
    db.WithPostgresMcp();
    keycloakDb.WithPostgresMcp();

    var mailpit = builder.AddMailPit("mailpit");

    keycloak
        .WithRealmImport("./realms.json")
        .WithContainerFiles("/opt/keycloak/themes/group-split", "./keycloak-themes/group-split")
        .WithSmtp(mailpit)
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
    // The stack's single public origin. Has to resolve to the same address from a browser
    // and from inside the Compose network -- "localhost" cannot: a container resolves it to
    // itself, and the two services below fetch OIDC metadata from this address at runtime.
    var hostname = builder.AddParameter("web-hostname");

    var authority = ReferenceExpression.Create(
        $"{hostname}{KeycloakDeploymentExtensions.RelativePath}/realms/group-split");

    // Unpublished, like the API: the web app's forwarder carries it, so the browser reaches
    // it on the web origin under its relative path. Keycloak's port stays off the host.
    keycloak
        .AsDeployedKeycloak(hostname)
        .WithSmtp(builder.Configuration);

    frontend
        .WithKeycloakAuthority(authority)
        .PublishOnHostPort(8080)
        // Exposed the way every other stack on the host is: joined to the shared `internal`
        // network so Caddy can dial it as group-split-web. The host port above stays until
        // the Caddyfile route and WEB_HOSTNAME point at the proxied hostname; then it can go.
        .WithExternalNetwork(compose, "internal", "group-split-web");

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

    backend.WithComposeHealthcheck(new Healthcheck
    {
        Test =
        [
            "CMD", "bash", "-c",
            @"{ printf 'HEAD /health HTTP/1.0\r\n\r\n' >&0; grep -q '^HTTP/1\.[01] 200'; } 0<>/dev/tcp/localhost/$$HealthChecks__Port"
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
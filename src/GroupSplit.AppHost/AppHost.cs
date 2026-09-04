using GroupSplit.AppHost.Deployment;
using GroupSplit.AppHost.Health;
using GroupSplit.AppHost.Keycloak;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Postgres;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIRECOMPUTE003, ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001, ASPIREPIPELINES001, ASPIREINTERACTION001

var builder = DistributedApplication.CreateBuilder(args);

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume();

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

var api = builder.AddProject<GroupSplit_API>("api")
    .WaitFor(db)
    .WithReference(db)
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WaitForCompletion(migrations)
    .WithHealthEndpoints();

var web = builder.AddProject<GroupSplit_App_Web>("web")
    .WithReference(keycloak)
    .WaitFor(api)
    .WithReference(api)
    .WithHealthEndpoints()
    .WithBrowserLogs();

if (builder.ExecutionContext.IsRunMode)
{
    db.WithPostgresMcp();
    keycloakDb.WithPostgresMcp();

    var mailpit = builder.AddMailPit("mailpit");

    keycloak.AsDevelopmentKeycloak(mailpit);

    builder
        .AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj")
        .AddWindowsDevice()
        .WithReference(api)
        .WaitFor(api);

    builder
        .AddSeeder<GroupSplit_Seeder>("seeder")
        .WaitFor(db)
        .WithReference(db)
        .WithResetAndSeedCommand()
        .WaitForCompletion(migrations);

    builder
        .AddScalarApiReference()
        .WithApiReference(api);
}
else
{
    var dashboardToken = builder
        .AddParameter("dashboard-token", secret: true)
        .WithDescription("Browser token for the published Aspire dashboard on port 18888.");

    var compose = builder.AddDockerComposeEnvironment("compose")
        .WithProtectedDashboard(dashboardToken);

    // The stack's single public origin. Has to resolve to the same address from a browser
    // and from inside the Compose network -- "localhost" cannot: a container resolves it to
    // itself, and the two services below fetch OIDC metadata from this address at runtime.
    var hostname = builder
        .AddParameter("web-hostname")
        .WithDescription(
            "Public origin of the web app, scheme included, e.g. https://groupsplit.example.com. "
            + $"Keycloak is served under it at {KeycloakDeploymentExtensions.RelativePath}, and it "
            + "must resolve from inside the Compose network as well as from a browser.");

    var authority = ReferenceExpression.Create(
        $"{hostname}{KeycloakDeploymentExtensions.RelativePath}/realms/group-split");

    // Unpublished, like the API: the web app's forwarder carries it, so the browser reaches
    // it on the web origin under its relative path. Keycloak's port stays off the host.
    keycloak
        .AsDeployedKeycloak(hostname)
        .WithSmtp(builder.Configuration);

    web
        .WithKeycloakAuthority(authority)
        // Exposed the way every other stack on the host is: joined to the shared `internal`
        // network so Caddy dials it as group-split-web -- and no host port, so nothing on
        // the LAN can bypass the proxy. WEB_HOSTNAME must resolve to the Caddyfile route's
        // hostname, or there is no path to the app at all.
        .WithExternalNetwork(compose, "internal", "group-split-web");

    // The API stays internal: the web app forwards to it and the WASM client uses its own origin.
    // It still validates browser-issued tokens, so it needs the same public issuer.
    api
        .WithKeycloakAuthority(authority)
        .WithManagementHealthcheck();

    dbServer.AsDeployedPostgres();

    // A remote host can only pull images it can reach, so publish tags into the shared
    // registry rather than leaving them tagged on whatever machine ran the deploy.
    var registry = builder.AddContainerRegistry("registry", "registry.sussman.win", "group-split");

    compose
        .WithContainerRegistry(registry)
        .WithComposeDefaults();

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
            $"push-{api.Resource.Name}",
            $"push-{web.Resource.Name}",
            $"push-{migrations.Resource.Name}",
        });
}

builder.Build().Run();
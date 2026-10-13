using GroupSplit.AppHost.Extensions;
using Projects;
using Scalar.Aspire;
using OpenAIExtensions = GroupSplit.AppHost.Extensions.OpenAIExtensions;

#pragma warning disable ASPIRECOMPUTE003, ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001, ASPIREPIPELINES001, ASPIREPIPELINES003, ASPIREINTERACTION001, ASPIREDOTNETPROJECT001

// Named once: the API reaches the bucket through the reference below, and the deployed
// storage container is told to create it by the same name.
const string receiptsBucket = "receipts";

var builder = DistributedApplication.CreateBuilder(args);

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithOtlpExporter();

var db = dbServer.AddDatabase("db", "groupsplit");

// The server and the bucket are held separately because only the server can be prepared for
// deployment, where the bucket has to be created by the container rather than by the
// orchestrator. See AsDeployedRustFs.
var storageServer = builder.AddRustFs("storage")
    .WithDataVolume();

var storage = storageServer.AddBucket(receiptsBucket);

var azureOpenAiParameters = builder.AddAzureOpenAIReceiptTranscriptionParameters();

// A Microsoft Foundry account, reached by its key. Receipt reading uses its OpenAI-compatible
// route; the name and parameters say Azure OpenAI because that is what they were first used for,
// and the deployment's secrets are named after them.
var azureOpenAi = OpenAIExtensions.AddOpenAI(builder, "azure-openai")
    .WithApiKey(azureOpenAiParameters.ApiKey)
    .WithEndpoint(azureOpenAiParameters.Endpoint);

var azureOpenAiReceiptModel = azureOpenAi.WithModel(
    "receipt-transcription", azureOpenAiParameters.Model);

// Microsoft-Decision-1, which suggests a group and category for an imported bank row. Not an
// OpenAI model: a Microsoft one on the same Foundry account, read with the same key. Empty
// leaves the suggestions off.
var azureOpenAiFilingModel = azureOpenAi.WithFoundryModel(
    "filing-suggestions", azureOpenAiParameters.DecisionDeployment);

// Veryfi remains SaaS-managed; this external resource gives its API calls a named dependency in
// the Aspire dashboard without routing or persisting receipt content there.
var veryfi = builder.AddExternalService("veryfi", new Uri("https://api.veryfi.com"));

// Plaid is SaaS-managed too. The API client selects Sandbox or Production from Plaid__Environment;
// this external resource is the local dashboard dependency and uses Plaid's Sandbox endpoint.
var plaid = builder.AddExternalService("plaid", new Uri("https://sandbox.plaid.com"));

var keycloakDb = dbServer.AddDatabase("keycloak-db", "keycloak");

// Backs the web app's sign-in ticket and token stores. Held in the web container's own
// memory, both went with it on every deploy: the cookie still decrypted, the session it
// named was gone, and everybody was challenged again.
var cache = builder.AddRedis("cache")
    .WithDataVolume()
    .WithPersistence(TimeSpan.FromSeconds(30))
    .WithOtlpExporter();

// What the API deletes a deleted account's Keycloak login with. Keycloak imports it into a fresh
// realm, the worker below holds an existing realm to it, and the API signs in with it.
var accountDeletionSecret = builder.AddAccountDeletionSecret();

var keycloak = builder.AddKeycloak("keycloak")
    .WithGoogleSignIn()
    .WithAccountDeletionSecret(accountDeletionSecret)
    .WithPostgres(keycloakDb)
    .WaitFor(keycloakDb)
    .WithOtlpExporter()
    // The realm records sign-in and admin events (realms.json), and its jboss-logging listener
    // writes each one to the container log as well. It logs a successful one at debug unless
    // told otherwise, which would leave only the failures there.
    .WithEnvironment("KC_SPI_EVENTS_LISTENER__JBOSS_LOGGING__SUCCESS_LEVEL", "info");

// Reconciles the allowlisted realm settings after Keycloak is ready. The same worker runs as
// a one-shot Compose service in deployment, and does not seed demo accounts or touch the app DB.
// Its admin credentials differ by mode, so each branch below gives it its own.
var realmSettings = builder.AddDotnetProject("realm-settings", "../GroupSplit.RealmWorker")
    .WithEnvironment("Realm__Operation", "reconcile")
    .WithAccountDeletionSecret(accountDeletionSecret)
    .WithReference(keycloak)
    // The switches realms.json's managed settings follow, e.g. SMTP_ENABLED for the password
    // reset link, with the values Keycloak's own import was given.
    .WithRealmVariablesOf(keycloak)
    .WaitFor(keycloak);

var migrations = db
    .AddEFMigrations("migrations",
        "../GroupSplit.Data.PostgreSQL.Migrations/GroupSplit.Data.PostgreSQL.Migrations.csproj",
        dbContextTypeName: "AppDbContext",
        connectionName: "DefaultConnection",
        configureProjectResource: project => project.ExcludeFromManifest())
    .RunDatabaseUpdateOnStart()
    .PublishAsMigrationBundle(publishContainer: true)
    .PublishAsDockerComposeService((_, service) => service.Restart = "no");

var api = builder.AddDotnetProject("api", "../GroupSplit.API")
    .WaitFor(db)
    .WithReference(db)
    .WithReference(keycloak)
    .WithReference(storage)
    .WithReference(veryfi)
    .WithReference(plaid)
    .WithReference(azureOpenAiReceiptModel)
    .WithReference(azureOpenAiFilingModel)
    .WaitFor(storage)
    .WaitFor(keycloak)
    .WaitForCompletion(migrations)
    .WithHealthEndpoints()
    // In both modes: locally the credentials come from user secrets and default to nothing,
    // which leaves the API running with bank sync reported as off.
    .WithPlaid()
    .WithVeryfiReceiptTranscription()
    .WithAzureOpenAIReceiptTranscription(azureOpenAiParameters)
    // Deleting an account deletes its Keycloak login, through a client that can manage users
    // in the app's realm and do nothing else.
    .WithAccountDeletionClient(accountDeletionSecret)
    // Do not serve traffic until the realm settings have been reconciled successfully -- which
    // includes the client above existing, with the secret just given to the API.
    .WaitForCompletion(realmSettings);

var web = builder.AddDotnetProject("web", "../GroupSplit.App/GroupSplit.App.Web")
    .WithReference(keycloak)
    .WaitFor(api)
    .WithReference(api)
    .WithReference(cache)
    .WaitFor(cache)
    .WithHealthEndpoints()
    .WithBrowserLogs();

if (builder.ExecutionContext.IsRunMode)
{
    // What the AI calls were asked and answered, on their spans, so the dashboard's GenAI
    // view has something to show. Locally only: it is people's merchants and amounts, and a
    // deployment's traces have no business holding them.
    api.WithEnvironment("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "true");

    // DBX for browsing, REPL for a psql or redis-cli shell in the dashboard's terminal dock,
    // signed in with the resource's own credentials.
    dbServer.WithDbx().WithRepl();
    cache.WithDbx().WithRepl();

    db.WithPostgresMcp();
    keycloakDb.WithPostgresMcp();

    var mailpit = builder.AddMailPit("mailpit");

    keycloak.AsDevelopmentKeycloak(mailpit);

    // Where a local sign-in may be sent back to: the web app's own endpoint, and nowhere else.
    // Given to Keycloak for a first-boot import and to the worker for a realm that already
    // exists, because the port is the test host's to choose on every run.
    //
    // As the browser sees it, pinned to the localhost network. Keycloak is a container, and an
    // endpoint handed to a container otherwise resolves to the address containers use to reach
    // the host -- so a first-boot import, or a realm reset, stored a redirect URI no browser
    // ever sends. HTTPS when the web app has it, which is every profile but the AppHost's plain
    // "http" one.
    var webScheme = web.Resource.Annotations.OfType<EndpointAnnotation>()
        .Any(endpoint => endpoint.Name == "https")
        ? "https"
        : "http";
    var webOrigin = web.GetEndpoint(webScheme, KnownNetworkIdentifiers.LocalhostNetwork);
    keycloak.WithWebAppOrigin(webOrigin);
    realmSettings
        .WithWebAppOrigin(webOrigin)
        .WithKeycloakAdminAccess(keycloak);

    var realmReset = builder.AddDotnetProject("realm-reset", "../GroupSplit.RealmWorker")
        .WithExplicitStart()
        .WithEnvironment("Realm__Operation", "reset")
        .WithReference(keycloak)
        .WithKeycloakAdminAccess(keycloak)
        .WaitFor(keycloak);

    realmSettings.WithLocalRealmResetCommand(keycloak, realmReset);

    builder
        .AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj")
        .AddWindowsDevice()
        .WithReference(api)
        .WaitFor(api);

    builder
        .AddSeeder<GroupSplit_Seeder>("seeder")
        .WaitFor(db)
        .WithReference(db)
        .WithKeycloakSeeding(keycloak)
        .WithResetAndSeedCommand()
        .WaitForCompletion(migrations);

    builder
        .AddCli<GroupSplit_Cli>("cli")
        .WithGroupSplitEndpoints(api, keycloak);

    builder
        .AddScalarApiReference()
        .WithApiReference(api);

    // Where bank providers are told to deliver webhooks locally, which is the web origin here
    // exactly as it is in a deployment. Off unless WebhookTunnel says otherwise.
    api.WithWebhookTunnel(web);
}
else
{
    var deploymentVersion = builder.Configuration["Deployment:Version"];
    if (string.IsNullOrWhiteSpace(deploymentVersion))
    {
        throw new InvalidOperationException(
            "Deployment:Version must be set when publishing. In CI, set Deployment__Version to an immutable release identifier.");
    }

    var dashboardToken = builder
        .AddParameter("dashboard-token", secret: true)
        .WithDescription("Browser token for the Aspire dashboard, published on the host's loopback port 18888.");

    var compose = builder.AddDockerComposeEnvironment("compose")
        .WithProtectedDashboard(dashboardToken);

    // Supplied rather than left to Aspire, which would generate a new one per publish and
    // not match the password the running container was started with.
    cache.WithPassword(
        builder
            .AddParameter("cache-password", secret: true)
            .WithDescription("Password for the Redis session cache."));

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
        .WithWebAppOrigin(hostname)
        .WithSmtp();

    // The account the realm worker signs in to the admin API as. Not Keycloak's bootstrap
    // admin: KC_BOOTSTRAP_ADMIN_* only creates a user when the master realm has none, so on a
    // Keycloak that already existed the bootstrap name and password need not belong to anybody
    // -- and production's admin is not called "admin". Signed in as a user that does not exist,
    // the worker exits non-zero and the API, which waits for it, never starts.
    var keycloakAdminUser = builder
        .AddParameter("keycloak-admin-user")
        .WithDescription("Username of an existing Keycloak master-realm admin, for the realm worker.");
    var keycloakAdminPassword = builder
        .AddParameter("keycloak-admin-password", secret: true)
        .WithDescription("Password of keycloak-admin-user.");

    realmSettings
        .WithEnvironment("Keycloak__AdminUser", keycloakAdminUser)
        .WithEnvironment("Keycloak__AdminPassword", keycloakAdminPassword)
        .WithWebAppOrigin(hostname)
        // Deployed, Keycloak answers only below its relative path, admin API included. Without
        // this the worker's first call is a 404, it exits non-zero, and the API -- which waits
        // for it to finish -- never starts.
        .WithEnvironment("Keycloak__RelativePath", KeycloakDeploymentExtensions.RelativePath)
        // The admin realm's frontend URL, so the admin console signs in through the SSH tunnel
        // rather than the public origin. See KeycloakDeploymentExtensions.AdminConsolePort.
        .WithEnvironment("Realm__AdminConsoleUrl", KeycloakDeploymentExtensions.AdminConsoleUrl)
        .WithRemoteImageTag(deploymentVersion)
        .PublishAsDockerComposeService((_, service) => service.Restart = "no");

    web
        .WithKeycloakAuthority(authority)
        // The sign-in, antiforgery and OIDC cookies are all protected with this ring, so
        // losing it on every deploy signs everybody out and breaks any sign-in in flight.
        .WithKeyRingVolume(compose, "web-keyring")
        // Exposed the way every other stack on the host is: joined to the shared `internal`
        // network so Caddy dials it as group-split-web -- and no host port, so nothing on
        // the LAN can bypass the proxy. WEB_HOSTNAME must resolve to the Caddyfile route's
        // hostname, or there is no path to the app at all.
        .WithExternalNetwork(compose, "internal", "group-split-web");

    // The API stays internal: the web app forwards to it and the WASM client uses its own origin.
    // It still validates browser-issued tokens, so it needs the same public issuer.
    api
        .WithKeycloakAuthority(authority)
        // Its admin calls go to Keycloak on the Compose network, which answers only below its
        // relative path -- the same reason the realm worker is told it.
        .WithEnvironment("Keycloak__RelativePath", KeycloakDeploymentExtensions.RelativePath)
        // Where bank providers are told to deliver webhooks: the same public origin, whose
        // /webhooks path the web app forwards here.
        .WithBankingOrigin(hostname)
        .WithManagementHealthcheck();

    dbServer.AsDeployedPostgres();

    storageServer
        .AsDeployedRustFs(receiptsBucket)
        .WithRustFsConsole();

    // A remote host can only pull images it can reach, so publish tags into the shared
    // registry rather than leaving them tagged on whatever machine ran the deploy.
    var registry = builder.AddContainerRegistry("registry", "ghcr.io", "sussman-club/group-split");

    // A production release supplies a semantic tag. The CI workflow resolves it to the
    // pushed registry digest before sending the Compose environment to the remote host.
    api.WithRemoteImageTag(deploymentVersion);
    web.WithRemoteImageTag(deploymentVersion);
    migrations.WithRemoteImageTag(deploymentVersion);

    compose
        .WithContainerRegistry(registry)
        .WithComposeDefaults()
        .WithPrepareOnPush();
}

builder.Build().Run();
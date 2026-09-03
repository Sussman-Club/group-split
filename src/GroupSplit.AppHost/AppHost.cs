using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

#pragma warning disable ASPIREPROBES001, ASPIRETERMINAL001, ASPIREPOSTGRES001, ASPIREBROWSERLOGS001

var builder = DistributedApplication.CreateBuilder(args);

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithPgWeb()
    .WithTerminal();

var db = dbServer.AddDatabase("db")
    .WithPostgresMcp();

var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithRealmImport("./realms.json")
    .WithDataVolume()
    .WithOtlpExporter()
    .WithTerminal();

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
    .WithBrowserLogs();

var mauiapp = builder.AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj");

mauiapp.AddWindowsDevice().WithReference(backend).WaitFor(backend);

var seeder = builder
    .AddSeeder<GroupSplit_Seeder>("seeder")
    .WaitFor(db)
    .WithReference(db)
    .WithResetAndSeedCommand();

if (builder.ExecutionContext.IsRunMode)
{
    var migrations = db
        .AddEFMigrations("migrations",
            "../GroupSplit.Data.PostgreSQL.Migrations/GroupSplit.Data.PostgreSQL.Migrations.csproj",
            dbContextTypeName: "AppDbContext",
            connectionName: "DefaultConnection")
        .RunDatabaseUpdateOnStart();

    // Nothing should touch the schema until migrations have been applied.
    backend.WaitForCompletion(migrations);
    seeder.WaitForCompletion(migrations);
}

var scalar = builder.AddScalarApiReference();

scalar.WithApiReference(backend);

var host = builder.Build();

host.Run();

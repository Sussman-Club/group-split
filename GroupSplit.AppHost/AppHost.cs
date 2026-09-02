using GroupSplit.AppHost;
using GroupSplit.AppHost.Docker;
using GroupSplit.AppHost.Migrations;
using GroupSplit.AppHost.Seeder;
using Projects;
using Scalar.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.AddDefaultServices();

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithPgWeb();

var db = dbServer.AddDatabase("db");

var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithRealmImport("./realms.json")
    .WithDataVolume()
    .WithOtlpExporter();

var backend = builder.AddProject<GroupSplit_API>("api")
    .WithDatabase(db)
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health");

var frontend = builder.AddProject<GroupSplit_App_Web>("web")
    .WithReference(keycloak)
    .WaitFor(backend)
    .WithReference(backend)
    .WithHttpHealthCheck("/health");

var mauiapp = builder.AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj");

mauiapp.AddWindowsDevice().WithReference(backend);

var seeder = builder
    .AddSeeder<GroupSplit_Seeder>("seeder")
    .WithDatabase(db)
    .WithResetAndSeedCommand();

if (builder.ExecutionContext.IsRunMode)
{
    var migrations = db
        .AddEFMigrations("migrations", new GroupSplit_Data_PostgreSQL_Migrations().ProjectPath, dbContextTypeName: "AppDbContext", connectionName: "DefaultConnection")
        .RunDatabaseUpdateOnStart();

    // Nothing should touch the schema until migrations have been applied.
    backend.WaitFor(migrations);
    seeder.WaitFor(migrations);
}

var scalar = builder.AddScalarApiReference();

scalar.WithApiReference(backend);

var host = builder.Build();

if (builder.ExecutionContext.IsRunMode)
{
    await host.EnsureDockerIsRunning();
}

host.Run();
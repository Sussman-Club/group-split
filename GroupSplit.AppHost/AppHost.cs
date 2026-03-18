using GroupSplit.AppHost;
using GroupSplit.AppHost.Docker;
using GroupSplit.AppHost.EntityFramework;
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

if (builder.ExecutionContext.IsRunMode)
{
    builder.AddEfInstaller("dotnet-ef-installer");

    db.AddMigrationOrchestration<PostgresDatabaseResource, GroupSplit_Data_PostgreSQL_Migrations>("AppDbContext")
        .WithDefaultCommands();
}

var keycloak = builder.AddKeycloak("keycloak", 8080)
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

var scalar = builder.AddScalarApiReference();

scalar.WithApiReference(backend);

var host = builder.Build();

if (builder.ExecutionContext.IsRunMode)
{
    await host.EnsureDockerIsRunning();
}

host.Run();
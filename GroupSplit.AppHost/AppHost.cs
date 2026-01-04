using GroupSplit.AppHost;
using GroupSplit.AppHost.Docker;
using GroupSplit.AppHost.EntityFramework;
using GroupSplit.AppHost.Seeder;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.AddDefaultServices();

var dbServer = builder
    .AddPostgres("db-server")
    .WithDataVolume()
    .WithPgWeb();

var db = dbServer.AddDatabase("db");
var identityDb = dbServer.AddDatabase("identity");

if (builder.ExecutionContext.IsRunMode)
{
    builder.AddEfInstaller("dotnet-ef-installer");

    db.AddMigrationOrchestration<PostgresDatabaseResource, GroupSplit_Data_PostgreSQL_Migrations>("AppDbContext")
        .WithDefaultCommands();

    identityDb.AddMigrationOrchestration<PostgresDatabaseResource, GroupSplit_Identity_Migrations_PostgreSQL>()
        .WithDefaultCommands();
}

var backend = builder.AddProject<GroupSplit_API>("api")
    .WithDatabase(db)
    .WithDatabase(identityDb)
    .WithScalarUrl()
    .WithHttpHealthCheck("/health");

var frontend = builder.AddProject<GroupSplit_App_Web>("web")
    .WaitFor(backend)
    .WithReference(backend)
    .WithHttpHealthCheck("/health");

var mauiapp = builder.AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj");

mauiapp.AddWindowsDevice().WithReference(backend);

var seeder = builder
    .AddSeeder<GroupSplit_Seeder>("seeder")
    .WithDatabase(db)
    .WithDatabase(identityDb)
    .WithResetAndSeedCommand();

var host = builder.Build();

if (builder.ExecutionContext.IsRunMode)
{
    await host.EnsureDockerIsRunning();
}

host.Run();
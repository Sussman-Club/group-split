using GroupSplit.AppHost;
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

    db.WithMigrationOrchestration<PostgresDatabaseResource, GroupSplit_Data_Migrations_PostgreSQL>();

    identityDb.WithMigrationOrchestration<PostgresDatabaseResource, GroupSplit_Identity_Migrations_PostgreSQL>();
}

var backend = builder.AddProject<GroupSplit_API>("api")
    .WaitFor(db)
    .WithReference(db)
    .WaitFor(identityDb)
    .WithReference(identityDb)
    .WithScalarUrl()
    .WithHttpHealthCheck("/health");

var frontend = builder.AddProject<GroupSplit_App_Web>("web")
    .WaitFor(backend)
    .WithReference(backend)
    .WithHttpHealthCheck("/health");

var mauiapp = builder.AddMauiProject("app", "../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj");

mauiapp.AddWindowsDevice()
    .WithReference(backend);

var host = builder.Build();

if (builder.ExecutionContext.IsRunMode)
{
    await host.EnsureDockerIsRunning();
}

host.Run();
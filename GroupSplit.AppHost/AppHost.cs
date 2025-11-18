using GroupSplit.AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var dbServer = builder.AddPostgres("db-server").WithDataVolume();
var db = dbServer.AddDatabase("db");
var identityDb = dbServer.AddDatabase("identity");

if (builder.ExecutionContext.IsRunMode)
{
    var installer = builder.AddEfInstaller("dotnet-ef-installer");
    db.AddMigrator<PostgresDatabaseResource, GroupSplit_Data_Migrations_PostgreSQL>().WaitForCompletion(installer);
    identityDb.AddMigrator<PostgresDatabaseResource, GroupSplit_Identity_Migrations_PostgreSQL>().WaitForCompletion(installer);
}

var backend = builder.AddProject<GroupSplit_API>("API")
    .WaitFor(db)
    .WaitFor(identityDb)
    .WithReference(identityDb)
    .WithScalarUrl();

builder.AddProject<GroupSplit_Web>("Web").WithReference(backend);

builder.Build().Run();

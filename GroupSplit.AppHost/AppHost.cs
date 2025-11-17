using GroupSplit.AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var dbServer = builder.AddPostgres("db-server");
var db = dbServer.AddDatabase("db");

if (builder.ExecutionContext.IsRunMode)
{
    var installer = builder.AddEfInstaller("dotnet-ef-installer");
    db.AddMigrator<PostgresDatabaseResource, GroupSplit_Data_Migrations_PostgreSQL>().WaitForCompletion(installer);
}

var backend = builder.AddProject<GroupSplit_API>("API")
    .WaitFor(db)
    .WithScalarUrl();

builder.AddProject<Projects.GroupSplit_App_Web>("web");

builder.Build().Run();

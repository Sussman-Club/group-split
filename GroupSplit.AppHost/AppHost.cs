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

var backend = builder.AddProject<GroupSplit_API>("api")
    .WaitFor(db)
    .WithScalarUrl();

builder.AddProject<GroupSplit_App_Web>("web");

var mauiapp = builder.AddMauiProject("app", @"../GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj");

mauiapp.AddWindowsDevice()
    .WithReference(backend);

builder.Build().Run();

using GroupSplit.AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var dbServer = builder.AddPostgres("db-server");
var db = dbServer.AddDatabase("db");

db.AddMigrator<PostgresDatabaseResource, GroupSplit_Data_Migrations_PostgreSQL>();

var backend = builder.AddProject<GroupSplit_API>("API")
    .WaitFor(db)
    .WithScalarUrl();

builder.AddProject<GroupSplit_Web>("Web").WithReference(backend);

builder.Build().Run();

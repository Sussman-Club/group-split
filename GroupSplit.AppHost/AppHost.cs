using GroupSplit.AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<GroupSplit_API>("API").WithScalarUrl();

builder.Build().Run();

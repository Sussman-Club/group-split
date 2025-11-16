using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<GroupSplit_API>("API");
builder.Build().Run();

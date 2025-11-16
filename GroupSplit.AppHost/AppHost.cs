using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<GroupSplit_API>("API");

builder.AddProject<GroupSplit_Web>("Web").WithReference(backend);

builder.Build().Run();

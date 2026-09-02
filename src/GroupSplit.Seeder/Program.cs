using GroupSplit.Data.PostgreSQL;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Orchestration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddPostgreSqlAppDbContext(builder.Configuration.GetConnectionString("db"));

builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));

var seederBuilder = builder.Services.AddSeederRunner();

seederBuilder.AddSeeders();

var host = builder.Build();
host.Run();
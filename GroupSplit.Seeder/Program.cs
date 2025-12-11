using GroupSplit.Data.PostgreSQL;
using GroupSplit.Identity;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Orchestration;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppIdentityContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("identity")));

builder.Services.AddPostgreSqlAppDbContext(builder.Configuration.GetConnectionString("db"));

builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));

var seederBuilder = builder.Services.AddSeederRunner();

seederBuilder.AddSeeders();

var host = builder.Build();
host.Run();
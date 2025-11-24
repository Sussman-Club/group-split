using GroupSplit.Data;
using GroupSplit.Identity;
using GroupSplit.Seeder;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppIdentityContext>(
    options => options.UseNpgsql(builder.Configuration.GetConnectionString("identity")),
    ServiceLifetime.Transient);

builder.Services.AddDbContext<AppDbContext>(
    options => options.UseNpgsql(builder.Configuration.GetConnectionString("db")),
    ServiceLifetime.Transient);

builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));

builder.Services.AddIdentityDatabaseSeeder();
builder.Services.AddDatabaseSeeders();

builder.Services.AddHostedService<DatabaseSeederRunner>();

var host = builder.Build();
host.Run();
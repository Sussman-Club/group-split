using GroupSplit.Data.PostgreSQL;
using GroupSplit.Seeder.Keycloak;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Orchestration;

var builder = Host.CreateApplicationBuilder(args);

builder.AddPostgreSqlAppDbContext("db");
builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));

// The demo accounts live in two places -- the app database and the realm -- and have to agree
// on one id, so the seeder writes both.
builder.AddKeycloakAdminClient();

var seederBuilder = builder.Services.AddSeederRunner();

seederBuilder.AddSeeders();

var host = builder.Build();
host.Run();

using GroupSplit.Data.PostgreSQL;
using GroupSplit.Keycloak;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Orchestration;
using GroupSplit.Seeder.Seeders;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKeycloakAdminClient();

builder.AddPostgreSqlAppDbContext("db");
builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));
builder.Services.Configure<KeycloakSeedOptions>(builder.Configuration.GetSection(KeycloakSeedOptions.SectionName));

// The demo accounts live in two places -- the app database and the realm -- and
// have to agree on one id, so the seeder writes both.
builder.Services.AddSeederRunner().AddSeeders();

var host = builder.Build();
host.Run();

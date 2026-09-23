using GroupSplit.Data.PostgreSQL;
using GroupSplit.Seeder.Keycloak;
using GroupSplit.Seeder.Options;
using GroupSplit.Seeder.Orchestration;
using GroupSplit.Seeder.Seeders;

var builder = Host.CreateApplicationBuilder(args);
builder.AddKeycloakAdminClient();

var seederBuilder = builder.Services.AddSeederRunner();

switch (builder.Configuration["Seeder:Mode"])
{
    case "realm-settings":
        seederBuilder.AddKeycloakRealmSettingsSeeder();
        break;

    case "reset-realm":
        seederBuilder.AddKeycloakRealmResetSeeder();
        break;

    default:
        builder.AddPostgreSqlAppDbContext("db");
        builder.Services.Configure<SeederOptions>(builder.Configuration.GetSection("Seeder"));

        // The demo accounts live in two places -- the app database and the realm -- and
        // have to agree on one id, so the seeder writes both.
        seederBuilder.AddSeeders();
        break;
}

var host = builder.Build();
host.Run();

using GroupSplit.Seeder.Options;

namespace GroupSplit.Seeder.Keycloak;

public static class KeycloakSeedingExtensions
{
    /// <summary>
    /// The name the AppHost's <c>WithReference(keycloak)</c> publishes the endpoint under.
    /// <c>https+http</c> takes whichever scheme the resource exposes, so this works whether
    /// Keycloak is served over TLS locally or not.
    /// </summary>
    private const string KeycloakEndpoint = "https+http://keycloak";

    extension(IHostApplicationBuilder builder)
    {
        public IHostApplicationBuilder AddKeycloakAdminClient()
        {
            builder.Services.Configure<KeycloakSeedOptions>(
                builder.Configuration.GetSection(KeycloakSeedOptions.SectionName));

            builder.Services.AddServiceDiscovery();

            builder.Services
                .AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>(client =>
                {
                    // A trailing slash, so the relative paths the client builds append rather
                    // than replace the last segment.
                    client.BaseAddress = new Uri(KeycloakEndpoint + "/");
                })
                .AddServiceDiscovery();

            return builder;
        }
    }
}
